using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per la lista Strumenti VIRTUALIZZATA con ricerca nel form Test (>10 strumenti a
/// catalogo, MostraRicercaStrumenti — profilazione 2026-09-25, ramo perf/6.4.0-editor-profilo appena
/// mergiato): ricerca su tutte le voci, spunta di una voce trovabile solo cercando, riepilogo selezionati,
/// salvataggio e riapertura. Semina 12 strumenti (soglia MostraRicercaStrumenti: Count > 10) via AppState,
/// stessa tecnica diretta di TestEditorOperazioniTests.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class TestEditorStrumentiVirtualizzatiTests : UiTestBase
{
    private const string StrumentoCercato = "Strumento-QA-Undicesimo";

    public TestEditorStrumentiVirtualizzatiTests() : base(CreaWorkspaceConDodiciStrumenti()) { }

    private static TestWorkspace CreaWorkspaceConDodiciStrumenti()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(TestEditorStrumentiVirtualizzatiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var strumenti = state.Master.Strumenti.Load();
        for (var i = 1; i <= 10; i++)
            strumenti.Add(new StrumentoDiagnostico { Nome = $"Strumento-QA-{i:00}", ModificatoDa = "UITests" });
        // L'undicesimo (>10 totali attiva MostraRicercaStrumenti) ha un nome distinguibile, trovabile SOLO
        // cercando (non visibile scrollando a colpo d'occhio i primi 10 senza filtro).
        strumenti.Add(new StrumentoDiagnostico { Nome = StrumentoCercato, Tipo = TipoStrumento.DaTestare, ModificatoDa = "UITests" });
        state.Master.Strumenti.Save(strumenti);

        return workspace;
    }

    [Fact]
    public void ListaVirtualizzata_RicercaSpuntaRiepilogoESalvataggio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var testList = shell.GoToTestList();
            var editor = testList.ClickNew();

            editor.ChooseTemplate();
            editor.GoToTab(CreateTestCaseFlow.TabDettagli);
            editor.SetDescrizioneBreve("Editor Strumenti virtualizzati - QA");
            editor.ChooseEcu(App, TestWorkspace.DefaultEcuName);

            Wait.ForCondition(() => editor.MostraListaStrumentiVirtualizzata,
                "con 11 strumenti a catalogo la lista virtualizzata con ricerca compare al posto della WrapPanel inline");

            editor.CercaStrumento(StrumentoCercato);
            Wait.ForCondition(() => editor.StrumentoVisibileInLista(StrumentoCercato),
                "lo strumento cercato compare nella lista filtrata");

            editor.SelezionaStrumentoInLista(StrumentoCercato);
            Wait.ForCondition(() => editor.StrumentiSelezionatiRiepilogo.Contains(StrumentoCercato),
                "il riepilogo selezionati riporta lo strumento appena spuntato");

            // Il riepilogo resta visibile anche svuotando la ricerca (tiene la selezione fuori filtro/vista,
            // vedi commento in TestCaseEditorView.axaml sopra la ListBox).
            editor.CercaStrumento("");
            Assert.Contains(StrumentoCercato, editor.StrumentiSelezionatiRiepilogo);

            editor.GoToTab(CreateTestCaseFlow.TabRiferimenti);
            editor.Save();
            // Salva() torna sempre alla Lista Test dopo un salvataggio riuscito (TestCaseEditorViewModel.Salva
            // chiama sempre _tornaAllaLista): l'editor da cui si e' appena salvato non e' piu' la finestra
            // corrente, quindi non c'e' nulla da "aggiornare" su di esso — si riapre direttamente dalla lista.

            var listaDopoSalvataggio = new TestListPage(App.WaitForMainWindow(), App.WaitForMainWindow());
            var riaperto = listaDopoSalvataggio.OpenOnlyRow();
            Wait.ForCondition(() => riaperto.MostraListaStrumentiVirtualizzata, "la lista virtualizzata resta attiva alla riapertura");
            Assert.Contains(StrumentoCercato, riaperto.StrumentiSelezionatiRiepilogo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ListaVirtualizzata_RicercaSpuntaRiepilogoESalvataggio), ex);
        }
    }
}
