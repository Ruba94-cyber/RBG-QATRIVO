using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Campo strumento di tipo "Lista di testo" (ListaTesto) nell'editor del Test: si apre l'editor delle righe
/// (ListaTestoEditorWindow), si incollano due righe, si aggiungono e si salva l'editor; il campo del Test deve riportare
/// due righe. Copre la riga D16 (ListaTestoEditorWindow) e la parte "righe di testo" di I51. Il salvataggio del Test non
/// è coperto: il Test seminato richiede una Funzionalità per essere salvato.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
[Trait("Area", "Strumenti")]
public sealed class CampoStrumentoListaTestoUiTests : UiTestBase
{
    private const string IdTest = "QA-LT-1";
    private const string Strumento = "ValidationTool QA";
    private const string NomeCampo = "Righe QA";
    private const string RigaUno = "prima riga di prova";
    private const string RigaDue = "seconda riga di prova";

    public CampoStrumentoListaTestoUiTests() : base(CreaWorkspace()) { }

    /// <summary>Uno strumento con un campo di righe di testo e un Test esistente da aprire nell'editor.</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(CampoStrumentoListaTestoUiTests), seedTestFixtures: true);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var strumenti = state.Master.Strumenti.Load();
        strumenti.Add(new StrumentoDiagnostico
        {
            Nome = Strumento,
            ModificatoDa = "UITests",
            Campi = new List<DefinizioneCampoStrumento> { new() { Nome = NomeCampo, Tipo = TipoCampoStrumento.ListaTesto } },
        });
        state.Master.Strumenti.Save(strumenti);
        // Un solo salvataggio del Test, già con il componente sulla condizione: ogni operazione deve averne una tra quelle del Test.
        var condizione = new Condizione { Esito = Esito.Conforme };
        condizione.Componente.Add(TestWorkspace.DefaultEcuName);
        var test = new TestCase
        {
            Id = IdTest,
            DescrizioneBreve = "Test con righe di testo",
            TestTemplateUid = TestCoverageSeed.UidTemplatePrincipale(state),
            CreatoDa = "QA Master",
            StatoLavoro = StatoLavoroTest.Pianificato,
            ModificatoDa = "UITests",
            DataCreazione = DateTime.Now,
            UltimaModifica = DateTime.Now,
        };
        test.Componente.Add(TestWorkspace.DefaultEcuName);
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1,
            Data = DateTime.Now,
            Operatore = "UITests",
            Esito = Esito.Conforme,
            Condizioni = new List<Condizione> { condizione },
        });
        state.TestCases.Save(test, true);
        return ws;
    }

    [Fact]
    public void CampoListaTesto_RigheIncollateNelRiquadroESalvateNelTest()
    {
        const string nomeTest = nameof(CampoListaTesto_RigheIncollateNelRiquadroESalvateNelTest);
        var passo = "apertura del Test in modifica";
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window, App);
            shell.DismissNotificationsIfShown();
            var editor = shell.GoToTestList().OpenRowById(IdTest);
            editor.EnterEditMode();
            editor.SelezionaStrumento(Strumento);

            passo = "apertura dell'editor delle righe dal campo di tipo ListaTesto";
            ElementFinders.WaitButtonById(window, $"Editor_Campo_{NomeCampo}").SafeInvoke();
            var righe = App.WaitForWindowByContent("ListaTesto_Incolla_TextBox");

            passo = "incolla e aggiungi le righe";
            ElementFinders.WaitTextBoxById(righe, "ListaTesto_Incolla_TextBox").SetText($"{RigaUno}\n{RigaDue}");
            ElementFinders.WaitButtonById(righe, "ListaTesto_AggiungiRighe_Button").SafeInvoke();
            Wait.ForCondition(() => righe.FindAllDescendants(cf => cf.ByAutomationId("ListaTesto_Riga_TextBox")).Length == 2,
                "due righe nell'elenco dell'editor");

            passo = "salvataggio dell'editor delle righe";
            ElementFinders.WaitButtonById(righe, "ListaTesto_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("ListaTesto_Incolla_TextBox"), "editor delle righe chiuso dopo Salva");

            // Le due righe arrivano al campo del Test: il pulsante riporta il conteggio. Il salvataggio del Test non è
            // verificato qui: il Test seminato richiede una Funzionalità per essere salvato (gap in matrice).
            passo = "conteggio righe sul campo del Test";
            Wait.ForCondition(() => ElementFinders.WaitButtonById(window, $"Editor_Campo_{NomeCampo}").Name.EndsWith("(2)", StringComparison.Ordinal),
                "il campo di tipo ListaTesto mostra le due righe");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nomeTest, ex, step: passo);
        }
    }
}
