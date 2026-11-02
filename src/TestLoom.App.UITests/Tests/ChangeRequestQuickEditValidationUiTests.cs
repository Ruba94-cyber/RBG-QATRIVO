using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class ChangeRequestQuickEditValidationUiTests : UiTestBase
{
    private const string IdOriginale = "CR-QUICK-DUP-1";
    private const string IdDaModificare = "CR-QUICK-DUP-2";
    private const string FinestraRapidaCr = "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox";

    public ChangeRequestQuickEditValidationUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(ChangeRequestQuickEditValidationUiTests));
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = "CR QA duplicati", IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateUid = templates.Last(t => t.Nome == "CR QA duplicati").Uid;

        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = IdOriginale,
            Titolo = "Integrazione gateway ADAS",
            TemplateUid = templateUid,
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName },
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = IdDaModificare,
            Titolo = "Richiesta iniziale",
            Descrizione = "Richiesta di test separata",
            TemplateUid = templateUid,
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName },
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-STATE-DUP",
            Titolo = "Integrazione gateway ADAS urgente",
            Descrizione = "Aggiornare comunicazione gateway ADAS sui veicoli Daily",
            Responsabile = TestWorkspace.DefaultOperatorName,
            TemplateUid = templateUid,
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName },
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QUICK-CLOSED",
            Titolo = "Richiesta chiusa",
            TemplateUid = templateUid,
            Stato = StatoChangeRequest.Chiusa,
            ChiusaIl = DateTime.Now,
            ChiusaDa = TestWorkspace.DefaultOperatorName,
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName },
            EcuImpattate = new() { TestWorkspace.DefaultEcuName },
        });
        return workspace;
    }

    /// <summary>La modifica rapida non espone piu' titolo e descrizione: il controllo duplicati con la motivazione resta
    /// nell'editor completo. Qui il Salva rapido registra un ticket su una CR che ha un duplicato, senza chiedere la
    /// motivazione e senza cambiare il titolo.</summary>
    [Fact]
    public void ModificaRapidaCr_SalvaTicketSenzaControlloDuplicatiETitoloInvariato()
    {
        try
        {
            var main = App.WaitForMainWindow();
            new MainShellPage(main).GoToAnomalie();
            ElementFinders.WaitButtonById(main, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(main, "Anomalie_Cr_FiltroTesto_Input").SetText(IdOriginale);
            var riga = RigaCr(main, IdOriginale);
            var modificaRapida = ElementFinders.WaitByIdWithinRow(riga, "Anomalie_Cr_ModificaRapida_Button").AsButton();
            Assert.True(modificaRapida.IsEnabled, "la modifica rapida è abilitata per la CR aperta");
            modificaRapida.SafeInvoke();

            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.False(ElementFinders.ExistsById(rapida, "Anomalie_Cr_ModificaRapida_Titolo_TextBox"),
                "la modifica rapida non espone il titolo: il controllo duplicati resta nell'editor completo");
            AggiungiTicketRapido(rapida, "TICKET-QUICK-DUP-1");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr),
                "il Salva rapido chiude la finestra senza chiedere la motivazione del duplicato");

            var salvata = LeggiCr(IdOriginale);
            Assert.Contains(salvata.TicketCollegati, t => t.Riferimento == "TICKET-QUICK-DUP-1");
            Assert.Equal("Integrazione gateway ADAS", salvata.Titolo);
            Assert.True(string.IsNullOrWhiteSpace(salvata.MotivazioneDuplicato), "la modifica rapida non registra una motivazione duplicato");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ModificaRapidaCr_SalvaTicketSenzaControlloDuplicatiETitoloInvariato), ex); }
    }

    /// <summary>Una CR chiusa: il Master vede Riapri nella modifica rapida, la riapertura richiede il motivo, dopo la
    /// riapertura "Apri" mostra l'editor della CR in sola lettura con i dati salvati.</summary>
    [Fact]
    public void CrChiusa_ModificaRapidaRiapreConMotivazioneEApriRestaConsultabile()
    {
        try
        {
            var main = App.WaitForMainWindow();
            new MainShellPage(main).GoToAnomalie();
            ElementFinders.WaitButtonById(main, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(main, "Anomalie_Cr_FiltroTesto_Input").SetText("CR-QUICK-CLOSED");
            var riga = RigaCr(main, "CR-QUICK-CLOSED");

            var modificaRapida = ElementFinders.WaitByIdWithinRow(riga, "Anomalie_Cr_ModificaRapida_Button").AsButton();
            Assert.True(modificaRapida.IsEnabled, "la modifica rapida è disponibile per riaprire una CR chiusa");
            modificaRapida.SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr("CR-QUICK-CLOSED").Stato);
            Assert.True(Visibile(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button"), "il Master vede Riapri su una CR chiusa");
            Assert.False(Visibile(rapida, "Anomalie_Cr_ModificaRapida_Implementa_Button"), "una CR chiusa non si implementa");

            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileConTesto(rapida, "Anomalie_Cr_ModificaRapida_Errore_Text"),
                "senza motivazione la riapertura resta bloccata e la finestra mostra il messaggio");
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr("CR-QUICK-CLOSED").Stato);

            ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_Motivo_TextBox").SetText("Riapertura per verifica QA");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => LeggiCr("CR-QUICK-CLOSED").Stato == StatoChangeRequest.Aperta,
                "la CR chiusa viene riaperta dalla modifica rapida con il motivo");
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "la modifica rapida si chiude dopo la riapertura");

            var editor = ChangeRequestEditorFlow.ApriInSolaLettura(main, "CR-QUICK-CLOSED", "CR-QUICK-CLOSED");
            Assert.Equal("Richiesta chiusa", ElementFinders.WaitTextBoxById(main, "Cr_Titolo_TextBox").Text);
            Assert.False(ElementFinders.WaitTextBoxById(main, "Cr_Titolo_TextBox").IsEnabled, "in sola lettura i dati non sono modificabili");
            Assert.True(editor.InSolaLettura, "Apri dopo la riapertura mostra l'editor in sola lettura");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrChiusa_ModificaRapidaRiapreConMotivazioneEApriRestaConsultabile), ex); }
    }

    /// <summary>Dopo un Salva rapido la lista mostra subito il ticket salvato; con modifiche non salvate, Annulla chiede
    /// conferma e la modifica successiva non viene registrata.</summary>
    [Fact]
    public void ModificaRapidaCr_ListaSiAggiornaDopoSalvataggioSeguitoDaAnnulla()
    {
        const string ticketSalvato = "TICKET-QUICK-SALVATO";
        const string ticketNonSalvato = "TICKET-QUICK-NONSALVATO";
        try
        {
            var main = App.WaitForMainWindow();
            new MainShellPage(main).GoToAnomalie();
            ElementFinders.WaitButtonById(main, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(main, "Anomalie_Cr_FiltroTesto_Input").SetText(IdDaModificare);
            ElementFinders.WaitByIdWithinRow(RigaCr(main, IdDaModificare), "Anomalie_Cr_ModificaRapida_Button").AsButton().SafeInvoke();

            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            AggiungiTicketRapido(rapida, ticketSalvato);
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "il Salva rapido chiude la finestra");
            Wait.ForCondition(() => LeggiCr(IdDaModificare).TicketCollegati.Any(t => t.Riferimento == ticketSalvato),
                "il primo salvataggio è persistito prima della chiusura della finestra");
            Wait.ForCondition(() => ContieneNellaRiga(main, IdDaModificare, ticketSalvato), "la lista mostra il ticket del primo salvataggio");

            ElementFinders.WaitByIdWithinRow(RigaCr(main, IdDaModificare), "Anomalie_Cr_ModificaRapida_Button").AsButton().SafeInvoke();
            var seconda = App.WaitForWindowByContent(FinestraRapidaCr);
            AggiungiTicketRapido(seconda, ticketNonSalvato);
            ElementFinders.WaitButtonById(seconda, "Anomalie_Cr_ModificaRapida_Annulla_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr),
                "annullare con modifiche non salvate chiude la finestra dopo la conferma");

            var dopo = LeggiCr(IdDaModificare);
            Assert.Single(dopo.TicketCollegati);
            Assert.Equal(ticketSalvato, dopo.TicketCollegati[0].Riferimento);
            Wait.ForCondition(() => !ContieneNellaRiga(main, IdDaModificare, ticketNonSalvato),
                "la riga non mostra la modifica annullata");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ModificaRapidaCr_ListaSiAggiornaDopoSalvataggioSeguitoDaAnnulla), ex); }
    }

    /// <summary>"Apri" su una CR chiusa apre l'editor in sola lettura; "Modifica" lo porta in modifica (il Master puo'
    /// farlo anche su una CR chiusa), e il form completo riapre la CR con il motivo nel campo dedicato.</summary>
    [Fact]
    public void CrChiusa_ApriEModificaSelezionaLaRiaperturaConMotivazione()
    {
        try
        {
            var main = App.WaitForMainWindow();
            var editor = ChangeRequestEditorFlow.ApriInSolaLettura(main, "CR-QUICK-CLOSED", "CR-QUICK-CLOSED");
            Assert.False(ElementFinders.WaitTextBoxById(main, "Cr_Titolo_TextBox").IsEnabled, "in sola lettura i dati della CR chiusa non sono modificabili");

            ChangeRequestEditorFlow.EntraInModifica(editor);
            Assert.False(ElementFinders.WaitTextBoxById(main, "Cr_Titolo_TextBox").IsEnabled,
                "nel form completo di una CR chiusa i dati restano in sola lettura");
            ElementFinders.WaitComboBoxById(main, "Cr_Stato_Combo").SelectByText("Aperta");
            ElementFinders.WaitTextBoxById(main, "Cr_MotivoTransizione_Text").SetText("Riapertura dal form completo QA");
            ElementFinders.WaitButtonById(main, "Cr_Salva_Button").SafeInvoke();

            Wait.ForCondition(() => LeggiCr("CR-QUICK-CLOSED").Stato == StatoChangeRequest.Aperta,
                "Salva riapre la CR dal form completo");
            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(ElementFinders.WaitById(main, "Cr_Messaggio_Text").Name),
                "il form completo mostra la conferma della riapertura");
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(CrChiusa_ApriEModificaSelezionaLaRiaperturaConMotivazione), ex); }
    }

    /// <summary>Implementa dalla modifica rapida su una CR con duplicato non completa la transizione: la finestra segnala il
    /// duplicato e offre "Apri per completare". Nel form completo la motivazione permette di implementare senza test
    /// obbligatori.</summary>
    [Fact]
    public void ImplementaRapidaCrDuplicata_OffreApriPerCompletareEImplementaNelFormCompleto()
    {
        const string id = "CR-QUICK-STATE-DUP";
        const string motivazione = "Richiesta distinta confermata dal responsabile QA";
        try
        {
            var main = App.WaitForMainWindow();
            new MainShellPage(main).GoToAnomalie();
            ElementFinders.WaitButtonById(main, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(main, "Anomalie_Cr_FiltroTesto_Input").SetText(id);
            ElementFinders.WaitByIdWithinRow(RigaCr(main, id), "Anomalie_Cr_ModificaRapida_Button").AsButton().SafeInvoke();

            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Implementa_Button").SafeInvoke();
            Wait.ForCondition(() => (ElementFinders.WaitById(rapida, "Anomalie_Cr_ModificaRapida_Errore_Text").Name ?? "").Contains(IdOriginale, StringComparison.Ordinal),
                "l'implementazione rapida segnala il duplicato");
            Assert.True(Visibile(rapida, "Anomalie_Cr_ModificaRapida_ApriEditor_Button"), "con un duplicato la modifica rapida offre Apri per completare");
            Assert.Equal(StatoChangeRequest.Aperta, LeggiCr(id).Stato);

            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_ApriEditor_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "Apri per completare chiude la modifica rapida");
            var editor = new ChangeRequestEditorPage(main);
            Assert.False(editor.InSolaLettura, "Apri per completare apre l'editor completo in modifica");

            ElementFinders.WaitTextBoxById(main, "Cr_MotivazioneDuplicato_TextBox").SetText(motivazione);
            ElementFinders.WaitComboBoxById(main, "Cr_Stato_Combo").SelectByText("Implementata");
            editor.Salva();

            Wait.ForCondition(() => LeggiCr(id).Stato == StatoChangeRequest.Implementata,
                "con la motivazione l'editor completo implementa la CR");
            var salvata = LeggiCr(id);
            Assert.Equal(motivazione, salvata.MotivazioneDuplicato);
            Assert.Empty(salvata.TestValidazione);
        }
        catch (Exception ex) { CaptureFailureAndRethrow(nameof(ImplementaRapidaCrDuplicata_OffreApriPerCompletareEImplementaNelFormCompleto), ex); }
    }

    private static AutomationElement RigaCr(Window window, string id)
    {
        var lista = ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox");
        return Wait.ForElement(() => lista.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
                .FirstOrDefault(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null),
            $"riga CR {id}");
    }

    /// <summary>Riga della lista CR che contiene sia l'Id indicato sia il testo cercato (es. un riferimento ticket).</summary>
    private static bool ContieneNellaRiga(Window window, string id, string testo) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem))
            .Any(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null && r.FindFirstDescendant(cf => cf.ByName(testo)) != null);

    /// <summary>Compila il riferimento e l'URL del ticket nella modifica rapida e preme Aggiungi (senza salvare).</summary>
    private static void AggiungiTicketRapido(Window rapida, string riferimento)
    {
        ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_TicketRif_TextBox").SetText(riferimento);
        ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_TicketUrl_TextBox").SetText($"https://ticketing.example/{riferimento}");
        ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_AggiungiTicket_Button").SafeInvoke();
    }

    private static bool Visibile(AutomationElement scope, string automationId)
    {
        var el = ElementFinders.TryFindById(scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    /// <summary>Messaggio visibile e non vuoto (un TextBlock nascosto puo' avere ancora il testo, ma non e' letto dall'utente).</summary>
    private static bool VisibileConTesto(AutomationElement scope, string automationId) =>
        Visibile(scope, automationId) && !string.IsNullOrWhiteSpace(ElementFinders.TryFindById(scope, automationId)!.Name);

    private ChangeRequest LeggiCr(string id)
    {
        using var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.ChangeRequests.GetById(id)
            ?? throw new InvalidOperationException($"CR {id} non trovata nel workspace di test.");
    }
}
