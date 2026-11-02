using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;

namespace TestLoom.App.UITests.Tests;

/// <summary>
/// Campi di uno strumento (Impostazioni → Strumenti → Gestisci campi): campo di tipo "Lista a tendina" con le sue opzioni
/// (editor delle opzioni aperto subito dalla scelta del tipo), salvataggio dell'editor e della pagina Strumenti, persistenza
/// dopo il riavvio; anteprima del form dello strumento (TestFormPreviewWindow) con chiusura senza modifiche.
/// Le opzioni si verificano sul file dello strumento, non solo sull'interfaccia.
/// </summary>
[Collection("TestLoom UI")]
[Trait("Category", "Forms")]
[Trait("Area", "Strumenti")]
public sealed class CampiStrumentoListaUiTests : IDisposable
{
    private const string Strumento = "ValidationTool QA";
    private const string NomeCampoLista = "Modo QA";
    private const string TipoListaTendina = "Lista a tendina";
    private readonly TestWorkspace _workspace;

    public CampiStrumentoListaUiTests() => _workspace = CreaWorkspace(conCampoLista: false);

    public void Dispose() => _workspace.Dispose();

    /// <summary>Uno strumento seminato; con <paramref name="conCampoLista"/> porta già il campo Lista "Modo QA" con due opzioni.</summary>
    private static TestWorkspace CreaWorkspace(bool conCampoLista)
    {
        var ws = TestWorkspace.CreateLoggedIn(nameof(CampiStrumentoListaUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(ws.ConfigPath), runBackgroundMaintenance: false);
        var strumento = new StrumentoDiagnostico { Nome = Strumento, ModificatoDa = "UITests" };
        if (conCampoLista)
        {
            strumento.Campi.Add(new DefinizioneCampoStrumento
            {
                Nome = NomeCampoLista, Tipo = TipoCampoStrumento.Lista, OpzioniLista = new List<string> { "Rosso", "Verde" },
            });
        }
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { strumento });
        return ws;
    }

    /// <summary>Dalla Home va in Impostazioni → Strumenti, mette in modifica il primo strumento e apre "Gestisci campi".</summary>
    private static AutomationElement ApriEditorCampi(TestLoomApplication app, Window principale, MainShellPage shell)
    {
        shell.NavigateTo("impostazioni");
        ElementFinders.WaitById(principale, "Impostazioni_Sub_strumenti").AsListBoxItem().Select();
        ElementFinders.WaitButtonById(principale, "Strum_Modifica_Button").SafeInvoke();
        Wait.ForCondition(() => ElementFinders.ExistsById(principale, "Strum_Salva_Button"), "pagina Strumenti in modifica");
        // Il clic può arrivare prima che la riga sia in modifica: si riprova finché compare l'editor dei campi.
        for (var tentativo = 1; ; tentativo++)
        {
            ElementFinders.WaitButtonById(principale, "Strumenti_GestisciCampi_Button").SafeInvoke();
            if (Wait.TryForCondition(() => !app.IsWindowClosedByContent("CampiStrumento_Annulla_Button"), TimeSpan.FromSeconds(5))) break;
            if (tentativo == 3) throw new TimeoutException("editor dei campi strumento non aperto dopo 3 tentativi da 'Gestisci campi'");
        }
        var editor = app.WaitForWindowByContent("CampiStrumento_Annulla_Button");
        Wait.ForCondition(() => ElementFinders.TryFindById(editor, "CampiStrumento_Titolo_Text") is { } titolo
            && !string.IsNullOrWhiteSpace(titolo.Name), "titolo dell'editor dei campi strumento valorizzato");
        return editor;
    }

    private static Exception Diagnostica(TestLoomApplication? app, string nomeTest, string step, Exception ex)
    {
        var dir = FailureArtifacts.Capture(app, nomeTest, ex, step: step);
        return new Exception($"{ex.Message}\n[Diagnostica salvata in: {dir}]", ex);
    }

    [Fact]
    public void CampoLista_ConDueOpzioni_SalvatoNelloStrumentoEPersisteDopoRiavvio()
    {
        const string nomeTest = nameof(CampoLista_ConDueOpzioni_SalvatoNelloStrumentoEPersisteDopoRiavvio);
        TestLoomApplication? app = null;
        var passo = "sessione 1 - nuovo campo Lista";
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var principale = app.WaitForMainWindow();
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();
            var editor = ApriEditorCampi(app, principale, shell);

            ElementFinders.WaitButtonById(editor, "CampiStrumento_Aggiungi_Button").SafeInvoke();
            ElementFinders.WaitTextBoxById(editor, "CampiStrumento_Riga_0_Nome_TextBox").SetText(NomeCampoLista);
            ElementFinders.WaitComboBoxById(editor, "CampiStrumento_Riga_0_Tipo_ComboBox").SelectByText(TipoListaTendina);

            // Passando a Lista un campo senza opzioni, l'editor delle opzioni si apre subito.
            passo = "editor delle opzioni dopo la scelta del tipo Lista";
            var opzioni = app.WaitForWindowByContent("ListaOpzioni_Testo_TextBox");
            ElementFinders.WaitTextBoxById(opzioni, "ListaOpzioni_Testo_TextBox").SetText("Rosso\nVerde");
            ElementFinders.WaitButtonById(opzioni, "ListaOpzioni_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => app.IsWindowClosedByContent("ListaOpzioni_Testo_TextBox"), "editor delle opzioni chiuso dopo Salva");

            passo = "riepilogo opzioni nel campo";
            Wait.ForCondition(() => ElementFinders.WaitButtonById(editor, "CampiStrumento_Riga_0_Opzioni_Button").Name.EndsWith("(2)", StringComparison.Ordinal),
                "pulsante delle opzioni del campo con il conteggio (2)");

            passo = "salvataggio dell'editor campi";
            ElementFinders.WaitButtonById(editor, "CampiStrumento_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => app.IsWindowClosedByContent("CampiStrumento_Annulla_Button"), "editor dei campi chiuso dopo Salva");

            // La pagina Strumenti salva lo strumento: torna in lettura.
            passo = "salvataggio della pagina Strumenti";
            ElementFinders.WaitButtonById(principale, "Strum_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(principale, "Strum_Modifica_Button"), "pagina Strumenti tornata in lettura dopo Salva");
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nomeTest, passo, ex);
        }
        finally
        {
            app?.Dispose();
        }

        // Verifica sul file: il campo esiste con tipo Lista e le sue due opzioni.
        using (var state = new AppState(new AppConfigService(_workspace.ConfigPath), runBackgroundMaintenance: false))
        {
            var salvato = state.Master.Strumenti.Load().Single(s => s.Nome == Strumento);
            var campo = salvato.Campi.Single(c => c.Nome == NomeCampoLista);
            Assert.Equal(TipoCampoStrumento.Lista, campo.Tipo);
            Assert.Equal(new[] { "Rosso", "Verde" }, campo.OpzioniLista);
        }

        // Riavvio: il campo e il conteggio delle opzioni si rivedono nell'editor.
        try
        {
            app = TestLoomApplication.Launch(_workspace);
            var principale = app.WaitForMainWindow();
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();
            var editor = ApriEditorCampi(app, principale, shell);
            Wait.ForCondition(() => ElementFinders.WaitTextBoxById(editor, "CampiStrumento_Riga_0_Nome_TextBox").Text == NomeCampoLista,
                "campo riletto dopo il riavvio");
            Wait.ForCondition(() => ElementFinders.WaitButtonById(editor, "CampiStrumento_Riga_0_Opzioni_Button").Name.EndsWith("(2)", StringComparison.Ordinal),
                "opzioni del campo riviste dopo il riavvio");
            ElementFinders.WaitButtonById(editor, "CampiStrumento_Annulla_Button").SafeInvoke();
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nomeTest, "sessione 2 - riapertura dopo riavvio", ex);
        }
        finally
        {
            app?.Dispose();
        }
    }

    [Fact]
    public void Anteprima_MostraIlCampoListaDelloStrumento_EChiudeSenzaCambiareNulla()
    {
        // Lo strumento parte già con il campo "Modo QA": qui si verifica solo l'anteprima del form.
        var ws = CreaWorkspace(conCampoLista: true);
        const string nomeTest = nameof(Anteprima_MostraIlCampoListaDelloStrumento_EChiudeSenzaCambiareNulla);
        TestLoomApplication? app = null;
        var passo = "apertura dell'anteprima";
        try
        {
            app = TestLoomApplication.Launch(ws);
            var principale = app.WaitForMainWindow();
            var shell = new MainShellPage(principale, app);
            shell.DismissNotificationsIfShown();
            var editor = ApriEditorCampi(app, principale, shell);

            ElementFinders.WaitButtonById(editor, "CampiStrumento_Anteprima_Button").SafeInvoke();
            var anteprima = app.WaitForWindowByContent("AnteprimaForm_Chiudi_Button");
            passo = "contenuto dell'anteprima";
            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(ElementFinders.WaitById(anteprima, "AnteprimaForm_Titolo_Text").Name),
                "titolo dell'anteprima valorizzato");
            Wait.ForCondition(() => anteprima.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                .Any(t => t.Name?.Contains(NomeCampoLista, StringComparison.Ordinal) == true), $"il campo '{NomeCampoLista}' compare nell'anteprima");

            passo = "chiusura dell'anteprima";
            ElementFinders.WaitButtonById(anteprima, "AnteprimaForm_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => app.IsWindowClosedByContent("AnteprimaForm_Chiudi_Button"), "anteprima chiusa con Chiudi");

            // Annulla dell'editor: nessuna modifica, lo strumento resta com'era.
            passo = "annulla dell'editor campi";
            ElementFinders.WaitButtonById(editor, "CampiStrumento_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => app.IsWindowClosedByContent("CampiStrumento_Annulla_Button"), "editor dei campi chiuso con Annulla");
        }
        catch (Exception ex)
        {
            throw Diagnostica(app, nomeTest, passo, ex);
        }
        finally
        {
            app?.Dispose();
            ws.Dispose();
        }
    }
}
