using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura P1 per la creazione di una Change Request e di una segnalazione dalla rete
/// assistenziale, entrambe prive di qualunque test FlaUI e di AutomationId fino a questa iterazione
/// (ChangeRequestEditorView/SegnalazioneReteEditorView). Si ferma al primo Salva riuscito: le transizioni di
/// stato successive della CR (Dichiara implementata -> Chiudi -> Riapri) richiedono un Responsabile assegnato
/// e un Test di validazione collegato con esito Conforme prima di poter chiudere (PuoChiudere richiede
/// Stato==Validata) — troppo per questa iterazione, restano GAP in COPERTURA_FLAUI.md.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class ChangeRequestSegnalazioneCreazioneTests : UiTestBase
{
    private const string TemplateCrNome = "CR Template QA";

    public ChangeRequestSegnalazioneCreazioneTests() : base(CreaWorkspaceConTemplateCr()) { }

    private static TestWorkspace CreaWorkspaceConTemplateCr()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(ChangeRequestSegnalazioneCreazioneTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = TemplateCrNome, IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        return workspace;
    }

    [Fact]
    public void NuovaChangeRequest_TitoloESalva_ResteRaggiungibileDaAnomalie()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();

            var editor = anomalie.NuovaChangeRequest();
            editor.SetTitolo("CR di QA - regressione automatica");
            editor.SetDescrizione("Descrizione tecnica della CR seminata dal test FlaUI.");
            // "Ambito impattato" e' obbligatorio (SalvaCommand rifiuta altrimenti con "Seleziona almeno un
            // veicolo, un componente o uno strumento impattato" — dialog "Campi obbligatori mancanti" mai gestito
            // da questo test, che si aspettava Salva() sempre riuscito).
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            editor.Salva();

            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(editor.StatoTesto), "lo stato della CR e' valorizzato dopo il salvataggio");

            editor.TornaAllaLista();
            Wait.ForCondition(() => NonConformitaPage_IsShown(window), "torna alla pagina Anomalie/CR dopo 'Torna alla lista'");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(NuovaChangeRequest_TitoloESalva_ResteRaggiungibileDaAnomalie), ex);
        }
    }

    [Fact]
    public void NuovaSegnalazioneRete_CampiObbligatoriESalva_CreaUnaNc()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var shell = new MainShellPage(window);
            var anomalie = shell.GoToAnomalie();

            var editor = anomalie.NuovaSegnalazioneRete();
            editor.AggiungiNumeroPratica("PRATICA-QA-0001");
            editor.AggiungiVin("ZFA0000000000QA01");
            editor.AggiungiLicenza("LIC-QA-0001");
            editor.SetDescrizioneBreve("Segnalazione QA - regressione automatica");
            editor.SetDescrizione("Descrizione dettagliata della segnalazione seminata dal test FlaUI.");
            editor.SetSeverita("Medium");
            editor.SetPriorita("Normal");
            editor.SetStato("InVerifica");
            // "Ambito impattato" e' obbligatorio anche qui ("Indica almeno un elemento di ambito: componente,
            // Veicolo, Funzionalità o Strumento" — stesso dialog "Campi obbligatori mancanti").
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            editor.Salva();

            // Dopo un salvataggio riuscito SalvaCommand riporta alla lista Anomalie/CR (stesso schema
            // dell'editor Test) — verifichiamo che la nuova NC (nata "Da rete") compaia cercandola.
            Wait.ForCondition(() => NonConformitaPage_IsShown(window), "torna alla pagina Anomalie/CR dopo il salvataggio");
            var listaAnomalie = new NonConformitaPage(window);
            listaAnomalie.Cerca("Segnalazione QA - regressione automatica");
            Wait.ForCondition(() => listaAnomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 300) == 1,
                "la segnalazione appena salvata compare come Non Conformita'");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(NuovaSegnalazioneRete_CampiObbligatoriESalva_CreaUnaNc), ex);
        }
    }

    private static bool NonConformitaPage_IsShown(FlaUI.Core.AutomationElements.Window window) =>
        ElementFinders.ExistsById(window, "Anomalie_Title_Text");
}
