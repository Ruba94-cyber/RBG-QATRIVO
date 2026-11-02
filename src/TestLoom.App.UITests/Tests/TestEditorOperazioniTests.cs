using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per le operazioni/condizioni dell'editor Test (scheda "Reiterazioni"): fino a questa
/// iterazione nessun test guidava l'esito di una condizione (incl. i valori 6.4.0 "Da eseguire" e
/// "Non applicabile") né aggiungeva/ripeteva/rimuoveva una condizione dalla UI reale — solo coperto a livello
/// di ViewModel in TestLoom.App.Tests. Copre anche il ciclo Salva→riapertura per verificare che l'esito
/// scelto sopravviva davvero su disco (non solo in memoria).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorOperazioniTests : UiTestBase
{
    private const string StrumentoQaNome = "StrumentoQA";

    public TestEditorOperazioniTests() : base(CreaWorkspaceConStrumentoCampiPersonalizzati()) { }

    /// <summary>Semina uno strumento con un campo per ciascun Tipo (Testo/Numero/Lista/Checkbox/Data/
    /// ListaTesto/File, tutti Ambito=PerOperazione) PRIMA di avviare TestLoom.exe (a differenza di seminare dopo,
    /// che richiederebbe di riavviare il processo a meta' test per fargli rileggere il file — vedi il pattern
    /// gia' usato da MasterDataAzioniModificaTests/StrumentiOrdineUiTests).</summary>
    private static TestWorkspace CreaWorkspaceConStrumentoCampiPersonalizzati()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestEditorOperazioniTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var strumenti = state.Master.Strumenti.Load();
        strumenti.Add(new StrumentoDiagnostico
        {
            Nome = StrumentoQaNome,
            ModificatoDa = "UITests",
            Campi = new List<DefinizioneCampoStrumento>
            {
                new() { Nome = "CampoTestoQA", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "CampoNumeroQA", Tipo = TipoCampoStrumento.Numero, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "CampoListaQA", Tipo = TipoCampoStrumento.Lista, Ambito = TipoAmbitoCampo.PerOperazione, OpzioniLista = new List<string> { "OpzioneA", "OpzioneB" } },
                new() { Nome = "CampoCheckboxQA", Tipo = TipoCampoStrumento.Checkbox, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "CampoDataQA", Tipo = TipoCampoStrumento.Data, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "CampoListaTestoQA", Tipo = TipoCampoStrumento.ListaTesto, Ambito = TipoAmbitoCampo.PerOperazione },
                new() { Nome = "CampoFileQA", Tipo = TipoCampoStrumento.File, Ambito = TipoAmbitoCampo.PerOperazione },
            },
        });
        strumenti.Add(new StrumentoDiagnostico
        {
            Nome = "UnitLogger",
            ModificatoDa = "UITests",
            Campi = new List<DefinizioneCampoStrumento>
            {
                new() { Nome = "Pacchetto", Tipo = TipoCampoStrumento.Testo, Ambito = TipoAmbitoCampo.PerOperazione },
            },
        });
        state.Master.Strumenti.Save(strumenti);

        return workspace;
    }

    [Fact]
    public void PrimaEsecuzione_EsitoDaEseguireDiDefault_PoiNonApplicabileRichiedeMotivazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();

            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Editor operazioni - esiti");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            // ChooseTemplate() ha gia' creato la prima reiterazione/condizione (SceglieTemplate ->
            // AggiungiIterazione+AggiungiCondizione, comportamento dell'app dalla 3.4.0): nessun "Avvia prima
            // esecuzione" da premere, il bottone non esiste piu' una volta scelto il tipo test.
            editor.GoToTab(2); // Reiterazioni
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "prima condizione creata alla scelta del template");

            // Esito di default di una condizione appena creata: Da eseguire (Esito.DaEseguire = 0), l'unico
            // esito 6.4.0 che non conta come "eseguito" insieme a Non applicabile (EsitoSemantica.EEseguito).
            Assert.False(editor.HaMotivazioneNonApplicabileVisibile(0), "La motivazione N.A. non deve comparire con esito 'Da eseguire'.");

            editor.SetEsitoCondizione(0, "Non applicabile");
            Wait.ForCondition(() => editor.HaMotivazioneNonApplicabileVisibile(0), "la motivazione N.A. compare dopo aver scelto l'esito 'Non applicabile'");
            editor.SetMotivazioneNonApplicabile(0, "Componente non equipaggiata in questa configurazione");

            editor.SetEsitoCondizione(0, "Conforme con deviazioni");
            Wait.ForCondition(() => editor.HaDescrizioneDeviazioneVisibile(0),
                "la descrizione della deviazione compare per l'esito 'Conforme con deviazioni'");
            editor.SetDescrizioneDeviazione(0, "Scostamento entro il limite concordato - QA");

            editor.SetEsitoCondizione(0, "Conforme");
            Wait.ForCondition(() => !editor.HaMotivazioneNonApplicabileVisibile(0), "la motivazione N.A. scompare tornando a un esito diverso da 'Non applicabile'");
            Wait.ForCondition(() => !editor.HaDescrizioneDeviazioneVisibile(0), "la descrizione della deviazione scompare tornando a 'Conforme'");

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();

            // Riapertura: l'esito scelto deve essere sopravvissuto al giro Salva -> torna alla Lista -> riapre.
            // Salva() torna sempre alla Lista Test, quindi si riapre da lì (nessun Refresh sull'editor: non è
            // più la finestra corrente).
            var listaDopoSalvataggio = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var riaperto = listaDopoSalvataggio.OpenOnlyRow();
            // GoToTab esiste solo per la striscia di schede della CREAZIONE (MostraSchedeCreazione): un test
            // ESISTENTE riaperto mostra tutte le sezioni insieme fin da subito (TestEditorPage.GoToTab, vedi
            // doc di classe) — "Editor_Tab_2_Button" non c'e' più, quindi non va cercato.
            Wait.ForCondition(() => riaperto.NumeroCondizioni() == 1, "la condizione salvata e' presente alla riapertura");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PrimaEsecuzione_EsitoDaEseguireDiDefault_PoiNonApplicabileRichiedeMotivazione), ex);
        }
    }

    [Fact]
    public void AggiungiCondizione_AumentaIlConteggio_RimuoviLoRiporta()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();

            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Editor operazioni - aggiungi/rimuovi condizione");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            editor.GoToTab(2);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "prima condizione creata alla scelta del template");

            editor.AggiungiCondizione();
            Wait.ForCondition(() => editor.NumeroCondizioni() == 2, "seconda condizione aggiunta");

            editor.RimuoviCondizione(1);
            Wait.ForCondition(() => editor.NumeroCondizioni() == 1, "una condizione rimossa, ne resta una sola (il pulsante Rimuovi resta nascosto con una sola condizione)");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(AggiungiCondizione_AumentaIlConteggio_RimuoviLoRiporta), ex);
        }
    }

}
