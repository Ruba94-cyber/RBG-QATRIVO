using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Editor della Change Request (ChangeRequestEditorView) non ancora guidato dalla UI: scelta del template
/// (due tipi CR), controllo CR simili con motivazione, ambito impattato dal flyout (veicoli) con rimozione, workflow
/// Implementa / Chiudi / Riapri (con Responsabile e con test di validazione obbligatorio eseguito Conforme), Archivia,
/// ticket TICKET/Ticketing, collegamento e creazione di un Test di validazione, indicatori di modifiche non salvate e di
/// Recupera bozza. Gli effetti sono verificati sui file change-requests/cr_*.json e testcases/**.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoverageChangeRequestUiTests : UiTestBase
{
    private const string TemplateAlfa = "CR Template QA";
    private const string TemplateBeta = "CR Template Beta";
    private const string EcuAmbito = "Componente-CR-IMPATTO";
    private const string TestValidazione = "TC-QA-VALIDAZIONE";
    private const string TestValidato = "TC-QA-VALIDATO";
    private const string Operatore = TestWorkspace.DefaultOperatorName;
    /// <summary>Le CR seminate condividono il componente e parole del titolo: il controllo CR simili blocca il salvataggio
    /// finche' non viene data una motivazione. Nei test che non verificano il blocco la motivazione e' sempre fornita.</summary>
    private const string MotivazioneQa = "Motivazione QA: CR di prova con ambito condiviso con le CR seminate";

    /// <summary>Uid del template Beta: letto dal file della CR dopo la scelta del tipo.</summary>
    private static Guid _uidTemplateBeta;

    public AnomalieCrCoverageChangeRequestUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageChangeRequestUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var componenti = state.Master.Componente.Load();
        if (!componenti.Any(e => e.Nome == EcuAmbito))
        {
            componenti.Add(new Componente { Nome = EcuAmbito, ModificatoDa = "UITests" });
            state.Master.Componente.Save(componenti);
        }

        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = TemplateAlfa, IsChangeRequest = true, ModificatoDa = "UITests" });
        templates.Add(new TestTemplate { Nome = TemplateBeta, IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var alfa = templates.Single(t => t.Nome == TemplateAlfa);
        _uidTemplateBeta = templates.Single(t => t.Nome == TemplateBeta).Uid;

        state.TestCases.Save(new TestCase
        {
            Id = TestValidazione, DescrizioneBreve = "Test di validazione collegabile", Componente = { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName, Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = "UITests", StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddDays(-3),
            UltimaModifica = DateTime.Now.AddDays(-3), ModificatoDa = "UITests",
        }, isNew: true);

        var condizioneConforme = new Condizione
        {
            Esito = Esito.Conforme, Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }), Nota = "Verifica conforme",
        };
        var esecuzioneConforme = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddDays(-1), Operatore = "UITests", Matricola = "VINQA000000000002",
        };
        esecuzioneConforme.Condizioni.Add(condizioneConforme);
        esecuzioneConforme.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = TestValidato, DescrizioneBreve = "Test di validazione eseguito Conforme", Componente = { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName, Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = "UITests", StatoLavoro = StatoLavoroTest.Concluso, DataCreazione = DateTime.Now.AddDays(-3),
            UltimaModifica = DateTime.Now.AddDays(-1), ModificatoDa = "UITests",
            Esecuzioni = new List<Esecuzione> { esecuzioneConforme },
        }, isNew: true);

        void Cr(string id, string titolo, StatoChangeRequest stato, string responsabile, TicketRif? ticket = null)
        {
            var cr = new ChangeRequest
            {
                Id = id, Titolo = titolo, Descrizione = "Descrizione " + titolo, TemplateUid = alfa.Uid,
                EcuImpattate = new() { TestWorkspace.DefaultEcuName }, Responsabile = responsabile, Stato = stato,
            };
            if (ticket != null) cr.TicketCollegati.Add(ticket);
            state.ChangeRequests.Save(cr);
        }

        Cr("CR-QA-0001", "CR Gamma di prova", StatoChangeRequest.Aperta, "");
        Cr("CR-QA-0002", "CR Delta responsabile", StatoChangeRequest.Aperta, Operatore);
        Cr("CR-QA-0004", "CR da archiviare", StatoChangeRequest.Aperta, "", new TicketRif
        {
            Riferimento = "TICKET-QA-0042", Url = "https://ticket.example.com/TICKET-QA-0042",
        });
        // Una sola scrittura: il repository rifiuta un secondo Save della stessa CR come se fosse nuova.
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QA-0003", Titolo = "CR Validata di prova", Descrizione = "Descrizione CR Validata di prova",
            TemplateUid = alfa.Uid, EcuImpattate = new() { TestWorkspace.DefaultEcuName }, Responsabile = Operatore,
            Stato = StatoChangeRequest.Validata, ImplementataIl = DateTime.Now.AddDays(-2), ImplementataDa = Operatore,
            // Il collegamento dichiara la copertura di componente e veicoli: senza, la valutazione resta incompleta e la CR
            // ricalcola lo stato in 'Da validare' all'apertura.
            TestValidazione = { new CollegamentoValidazioneCr { TestId = TestValidato, Obbligatorio = true, EcuCoperte = { TestWorkspace.DefaultEcuName } } },
        });
        return workspace;
    }

    private static IReadOnlyList<string> IdCr(Window window) => ChangeRequestEditorFlow.IdCr(window);

    private static bool VisibileById(Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private string PercorsoCr(string id) => new DataPaths(Workspace.WorkspaceDir).ChangeRequestFile(id);

    private ChangeRequest LeggiCr(string id) =>
        JsonIo.TryRead<ChangeRequest>(PercorsoCr(id), out var errore)
        ?? throw new InvalidOperationException($"CR {id} non leggibile da disco: {errore}");

    private ChangeRequest LeggiCrPerTitolo(string titolo) =>
        Directory.GetFiles(new DataPaths(Workspace.WorkspaceDir).ChangeRequestDir, "cr_*.json")
            .Select(f => JsonIo.TryRead<ChangeRequest>(f, out _)!)
            .Single(c => c.Titolo == titolo);

    private int FileCr() => Directory.GetFiles(new DataPaths(Workspace.WorkspaceDir).ChangeRequestDir, "cr_*.json").Length;

    private int FileTest() =>
        Directory.GetFiles(new DataPaths(Workspace.WorkspaceDir).TestCasesDir, "testcase_*.json", SearchOption.AllDirectories).Length;

    /// <summary>Porta in modifica la CR <paramref name="id"/> dalla scheda Change Request (percorso condiviso in ChangeRequestEditorFlow):
    /// filtra per testo, preme Apri nella riga della CR e poi Modifica nell'editor in sola lettura.</summary>
    private ChangeRequestEditorPage ApriCr(string id, string testoRicerca) =>
        ChangeRequestEditorFlow.ApriInModifica(App.WaitForMainWindow(), id, testoRicerca);

    /// <summary>Salva la CR fornendo la motivazione richiesta dal controllo CR simili (vedi MotivazioneQa).</summary>
    private static void SalvaCr(Window window, ChangeRequestEditorPage editor)
    {
        ElementFinders.WaitTextBoxById(window, "Cr_MotivazioneDuplicato_TextBox").SetText(MotivazioneQa);
        editor.Salva();
    }

    /// <summary>Flyout multi-selezione 'Aggiungi veicoli/componente' (checkbox anonime cercate per nome, come AggiungiEcu).</summary>
    private static void SelezionaDalFlyout(Window window, string bottoneId, string voce)
    {
        ElementFinders.WaitButtonById(window, bottoneId).SafeInvoke();
        var check = Wait.Until(
            () => window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox).And(cf.ByName(voce))),
            _ => true, description: $"CheckBox '{voce}' nel flyout di '{bottoneId}'");
        check.AsCheckBox().Toggle();
        ElementFinders.WaitButtonById(window, "MultiSelectPicker_Add_Button").SafeInvoke();
    }

    /// <summary>Sceglie una voce di un ComboBox i cui elementi non espongono un nome: si apre il combo, si cerca il testo
    /// della voce e si seleziona il ListItem che lo contiene tramite il pattern di selezione.</summary>
    private static void SelezionaVoceCombo(Window window, string comboId, string testo)
    {
        var combo = ElementFinders.WaitComboBoxById(window, comboId);
        combo.Expand();
        var voce = Wait.Until(
            () => combo.Items.FirstOrDefault(item =>
                item.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text).And(cf.ByName(testo))) != null),
            item => item != null, description: $"voce '{testo}' del combo {comboId}");
        if (!voce.Patterns.SelectionItem.IsSupported)
            throw new InvalidOperationException($"la voce '{testo}' del combo {comboId} non e' selezionabile");
        voce.Patterns.SelectionItem.Pattern.Select();
    }

    private void ConfermaSi()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma (Si)");
    }

    [Fact]
    public void NuovaCr_SceltaDelTemplateBeta_PersisteNelFileDellaCr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToAnomalie().NuovaChangeRequest();
            ElementFinders.WaitComboBoxById(window, "Cr_Template_Combo").SelectByText(TemplateBeta);
            editor.SetTitolo("CR con template Beta");
            editor.SetDescrizione("Descrizione del template Beta");
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            SalvaCr(window, editor);

            Wait.ForCondition(() => Directory.GetFiles(new DataPaths(Workspace.WorkspaceDir).ChangeRequestDir, "cr_*.json")
                .Select(f => JsonIo.TryRead<ChangeRequest>(f, out _)).Any(c => c?.Titolo == "CR con template Beta"),
                "la CR con il template Beta e' salvata su disco");
            Assert.Equal(_uidTemplateBeta, LeggiCrPerTitolo("CR con template Beta").TemplateUid);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(NuovaCr_SceltaDelTemplateBeta_PersisteNelFileDellaCr), ex);
        }
    }

    [Fact]
    public void Salvataggio_CrSimileAUnaEsistente_ChiedeLaMotivazioneDuplicato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToAnomalie().NuovaChangeRequest();
            ElementFinders.WaitComboBoxById(window, "Cr_Template_Combo").SelectByText(TemplateAlfa);
            editor.SetTitolo("CR Delta responsabile di prova");
            editor.SetDescrizione("Descrizione Delta condivisa");
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            var primaDelSalvataggio = FileCr();

            editor.Salva();
            Wait.ForCondition(() => VisibileById(window, "Cr_Errore_Text")
                && (ElementFinders.WaitById(window, "Cr_Errore_Text").Name ?? "").Contains("duplicata"),
                "una CR con titolo e ambito simili a una esistente chiede di indicare la motivazione");
            Wait.ForCondition(() => App.SafeFocusedElementId() == "Cr_MotivazioneDuplicato_TextBox",
                "dopo l'avviso il cursore si sposta direttamente sulla motivazione");
            Assert.Equal(primaDelSalvataggio, FileCr());

            ElementFinders.WaitTextBoxById(window, "Cr_MotivazioneDuplicato_TextBox").SetText("Variante della CR Delta per il test");
            editor.Salva();
            Wait.ForCondition(() => FileCr() == primaDelSalvataggio + 1, "con la motivazione la CR viene salvata");
            Assert.False(string.IsNullOrWhiteSpace(LeggiCrPerTitolo("CR Delta responsabile di prova").MotivazioneDuplicato));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Salvataggio_CrSimileAUnaEsistente_ChiedeLaMotivazioneDuplicato), ex);
        }
    }

    [Fact]
    public void Cr_EstaCollegato_EsponeUnLinkDirettoNellaTabella()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("CR-QA-0004");
            var link = ElementFinders.WaitButtonById(window, "Anomalie_Cr_EstaLink_Button");

            Assert.Equal("TICKET-QA-0042", link.Name);
            Assert.True(link.IsEnabled, "il riferimento TICKET è un collegamento apribile dalla riga CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Cr_EstaCollegato_EsponeUnLinkDirettoNellaTabella), ex);
        }
    }

    [Fact]
    public void Ambito_VeicoloDalFlyout_Viene_AggiuntoPoiRimossoEPersistito()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriCr("CR-QA-0001", "Gamma");
            Assert.False(VisibileById(window, $"Cr_RimuoviVeicolo_{TestWorkspace.DefaultVehicleName}"), "all'apertura non c'e' il veicolo");

            SelezionaDalFlyout(window, "Cr_AggiungiVeicoli_Button", TestWorkspace.DefaultVehicleName);
            Wait.ForCondition(() => VisibileById(window, $"Cr_RimuoviVeicolo_{TestWorkspace.DefaultVehicleName}"),
                "il veicolo scelto nel flyout compare come badge");
            SalvaCr(window, editor);
            Wait.ForCondition(() => LeggiCr("CR-QA-0001").VeicoliImpattati.Contains(TestWorkspace.DefaultVehicleName),
                "il salvataggio scrive il veicolo nell'ambito della CR");

            ElementFinders.WaitButtonById(window, $"Cr_RimuoviVeicolo_{TestWorkspace.DefaultVehicleName}").SafeInvoke();
            Wait.ForCondition(() => !VisibileById(window, $"Cr_RimuoviVeicolo_{TestWorkspace.DefaultVehicleName}"),
                "la rimozione toglie il badge del veicolo");
            SalvaCr(window, editor);
            Wait.ForCondition(() => !LeggiCr("CR-QA-0001").VeicoliImpattati.Contains(TestWorkspace.DefaultVehicleName),
                "la rimozione viene salvata nel file della CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Ambito_VeicoloDalFlyout_Viene_AggiuntoPoiRimossoEPersistito), ex);
        }
    }

    [Fact]
    public void Ambito_EcuDalFlyout_Viene_AggiuntaEPoiRimossa_PersistendoEntrambiISalvataggi()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriCr("CR-QA-0001", "Gamma");
            Assert.Contains(TestWorkspace.DefaultEcuName, LeggiCr("CR-QA-0001").EcuImpattate);
            Assert.DoesNotContain(EcuAmbito, LeggiCr("CR-QA-0001").EcuImpattate);

            editor.AggiungiEcu(App, EcuAmbito);
            Wait.ForCondition(() => VisibileById(window, $"Cr_RimuoviEcu_{EcuAmbito}"),
                "la nuova componente selezionata compare nell'ambito della CR");
            SalvaCr(window, editor);
            Wait.ForCondition(() => LeggiCr("CR-QA-0001").EcuImpattate.Contains(EcuAmbito),
                "il primo salvataggio persiste la nuova componente nell'ambito della CR");
            Assert.Contains(EcuAmbito, LeggiCr("CR-QA-0001").EcuImpattate);

            editor.RimuoviEcu(EcuAmbito);
            Wait.ForCondition(() => !VisibileById(window, $"Cr_RimuoviEcu_{EcuAmbito}"),
                "la rimozione toglie il badge del componente dall'editor");
            SalvaCr(window, editor);
            Wait.ForCondition(() => !LeggiCr("CR-QA-0001").EcuImpattate.Contains(EcuAmbito),
                "il secondo salvataggio persiste la rimozione del componente dall'ambito della CR");
            Assert.DoesNotContain(EcuAmbito, LeggiCr("CR-QA-0001").EcuImpattate);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Ambito_EcuDalFlyout_Viene_AggiuntaEPoiRimossa_PersistendoEntrambiISalvataggi), ex);
        }
    }

    [Fact]
    public void StatoNelFormCr_RispettaPermessiEConfermaImplementazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriCr("CR-QA-0001", "Gamma");
            var statoSenzaResponsabile = ElementFinders.WaitComboBoxById(window, "Cr_Stato_Combo");
            Assert.False(statoSenzaResponsabile.Items.Any(item => item.Text == "Implementata"),
                "senza Responsabile lo stato Implementata non è selezionabile");
            ElementFinders.WaitButtonById(window, "Cr_TornaAllaLista_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Torna alla lista riporta alla pagina Anomalie");

            ApriCr("CR-QA-0002", "Delta");
            var stato = ElementFinders.WaitComboBoxById(window, "Cr_Stato_Combo");
            Wait.ForCondition(() => stato.Items.Any(item => item.Text == "Implementata"),
                "con Responsabile lo stato Implementata è selezionabile dal form");
            ElementFinders.WaitTextBoxById(window, "Cr_MotivazioneDuplicato_TextBox").SetText(MotivazioneQa);
            stato.SelectByText("Implementata");
            ElementFinders.WaitButtonById(window, "Cr_Salva_Button").SafeInvoke();

            Wait.ForCondition(() => LeggiCr("CR-QA-0002").Stato == StatoChangeRequest.Implementata,
                "Salva applica la transizione Implementata scelta nel form CR");
            Wait.ForCondition(() => !string.IsNullOrWhiteSpace(ElementFinders.WaitById(window, "Cr_Messaggio_Text").Name),
                "il form conferma l'implementazione con un messaggio visibile");
            var salvata = LeggiCr("CR-QA-0002");
            Assert.Equal(Operatore, salvata.ImplementataDa);
            Assert.NotNull(salvata.ImplementataIl);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(StatoNelFormCr_RispettaPermessiEConfermaImplementazione), ex);
        }
    }

    [Fact]
    public void ChiudiERiapri_DaQualunqueStatoAttivo_RichiedeMotivoEMarcaSenzaValidazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ComboBox ComboStato() => ElementFinders.WaitComboBoxById(window, "Cr_Stato_Combo");

            // Master, CR Aperta e non validata: la chiusura si fa scegliendo Chiusa nel form con un motivo scritto.
            // Senza motivo il salvataggio e' rifiutato con un errore e la CR resta Aperta.
            var editor = ApriCr("CR-QA-0002", "Delta");
            Wait.ForCondition(() => ComboStato().Items.Any(item => item.Text == "Chiusa"),
                "un Master puo' chiudere una CR Aperta direttamente dal form");
            ComboStato().SelectByText("Chiusa");
            Wait.ForCondition(() => editor.MotivoTransizioneVisibile, "chiudere richiede un motivo scritto");
            editor.Salva();
            Wait.ForCondition(() => editor.Errore.Length > 0, "senza motivo la chiusura e' rifiutata con un errore");
            Assert.Equal(StatoChangeRequest.Aperta, LeggiCr("CR-QA-0002").Stato);

            editor.ImpostaMotivoTransizione("Chiusura QA: lavoro sospeso");
            editor.Salva();
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").Stato == StatoChangeRequest.Chiusa, "chiudere salva lo stato Chiusa");
            var chiusa = LeggiCr("CR-QA-0002");
            Assert.NotNull(chiusa.ChiusaIl);
            Assert.Equal("Chiusura QA: lavoro sospeso", chiusa.MotivoChiusura);
            Assert.True(chiusa.ChiusaSenzaValidazione, "una CR chiusa senza essere Validata viene marcata senza validazione");

            // Riaprire una CR chiusa richiede di nuovo un motivo scritto.
            Wait.ForCondition(() => ComboStato().Items.Any(item => item.Text == "Aperta"), "una CR chiusa puo' tornare Aperta dal form");
            ComboStato().SelectByText("Aperta");
            editor.Salva();
            Wait.ForCondition(() => editor.Errore.Length > 0, "senza motivo la riapertura e' rifiutata con un errore");
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr("CR-QA-0002").Stato);

            editor.ImpostaMotivoTransizione("Riapertura QA: ripresa del lavoro");
            editor.Salva();
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").Stato == StatoChangeRequest.Aperta, "riaprire salva lo stato Aperta");
            Assert.Null(LeggiCr("CR-QA-0002").ChiusaIl);
            Assert.False(LeggiCr("CR-QA-0002").ChiusaSenzaValidazione);

            // Una CR Validata si chiude con motivo, senza la marcatura "senza validazione".
            ElementFinders.WaitButtonById(window, "Cr_TornaAllaLista_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "torna alla lista prima della CR validata");
            var validata = ApriCr("CR-QA-0003", "Validata");
            Wait.ForCondition(() => ComboStato().Items.Any(item => item.Text == "Chiusa"), "una CR Validata puo' essere chiusa dal form");
            ComboStato().SelectByText("Chiusa");
            validata.ImpostaMotivoTransizione("Chiusura QA: CR validata chiusa a fine lavoro");
            validata.Salva();
            Wait.ForCondition(() => LeggiCr("CR-QA-0003").Stato == StatoChangeRequest.Chiusa, "chiudere la CR validata salva lo stato Chiusa");
            Assert.NotNull(LeggiCr("CR-QA-0003").ChiusaIl);
            Assert.False(LeggiCr("CR-QA-0003").ChiusaSenzaValidazione, "una CR validata non e' marcata senza validazione");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ChiudiERiapri_DaQualunqueStatoAttivo_RichiedeMotivoEMarcaSenzaValidazione), ex);
        }
    }

    [Fact]
    public void Archivia_NascondeLaCrDallaListaEScriveIlFileArchiviato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            ApriCr("CR-QA-0004", "archiviare");
            ElementFinders.WaitButtonById(window, "Cr_Archivia_Button").SafeInvoke();
            ConfermaSi();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "archiviando si torna alla lista");
            Wait.ForCondition(() => LeggiCr("CR-QA-0004").Archiviata, "il file della CR risulta archiviato");
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("archiviare");
            Wait.ForCondition(() => IdCr(window).Count == 0, "la CR archiviata non compare piu' nella scheda CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Archivia_NascondeLaCrDallaListaEScriveIlFileArchiviato), ex);
        }
    }

    [Fact]
    public void Ticket_AggiungiERimuovi_PersisteNelFileDellaCr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriCr("CR-QA-0002", "Delta");
            ElementFinders.WaitTextBoxById(window, "Cr_NuovoTicket_Riferimento_TextBox").SetText("TICKET-888");
            ElementFinders.WaitTextBoxById(window, "Cr_NuovoTicket_Url_TextBox").SetText("https://ticketing.example/TICKET-888");
            ElementFinders.WaitButtonById(window, "Cr_AggiungiTicket_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Cr_RimuoviTicket_TICKET-888"), "il ticket aggiunto compare nell'elenco");
            SalvaCr(window, editor);
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").TicketCollegati.Any(t => t.Riferimento == "TICKET-888"),
                "il salvataggio scrive il ticket nel file della CR");
            ElementFinders.WaitButtonById(window, "Cr_TornaAllaLista_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "torna alla lista dopo il salvataggio");

            var riaperta = ApriCr("CR-QA-0002", "Delta");
            ElementFinders.WaitButtonById(window, "Cr_RimuoviTicket_TICKET-888").SafeInvoke();
            Wait.ForCondition(() => !VisibileById(window, "Cr_RimuoviTicket_TICKET-888"), "la rimozione toglie il ticket dall'elenco");
            SalvaCr(window, riaperta);
            Wait.ForCondition(() => !LeggiCr("CR-QA-0002").TicketCollegati.Any(t => t.Riferimento == "TICKET-888"),
                "la rimozione viene salvata nel file della CR");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Ticket_AggiungiERimuovi_PersisteNelFileDellaCr), ex);
        }
    }

    [Fact]
    public void TestDiValidazione_CollegaUnTestEsistente_ComparaNellElencoEPersiste()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriCr("CR-QA-0002", "Delta");
            SelezionaVoceCombo(window, "Cr_CollegaTest_Combo", TestValidazione);
            ElementFinders.WaitButtonById(window, "Cr_CollegaTestObbligatorio_Button").SafeInvoke();

            Wait.ForCondition(() => VisibileById(window, $"Cr_ApriTest_{TestValidazione}"),
                "il test di validazione scelto compare nell'elenco dei test");
            SalvaCr(window, editor);
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").TestValidazione.Any(l => l.TestId == TestValidazione),
                "il salvataggio scrive il collegamento al test di validazione");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TestDiValidazione_CollegaUnTestEsistente_ComparaNellElencoEPersiste), ex);
        }
    }

    [Fact]
    public void TestDiValidazione_CreaDalTemplate_AggiungeUnTestNelWorkspace()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriCr("CR-QA-0002", "Delta");
            var testPrima = FileTest();
            SelezionaVoceCombo(window, "Cr_TemplateTest_Combo", "QA Smoke Template");
            // La creazione del test valida la CR come il salvataggio: la motivazione richiesta dal controllo CR simili e' fornita.
            ElementFinders.WaitTextBoxById(window, "Cr_MotivazioneDuplicato_TextBox").SetText(MotivazioneQa);
            ElementFinders.WaitButtonById(window, "Cr_CreaTestValidazione_Button").SafeInvoke();

            Wait.ForCondition(() => FileTest() == testPrima + 1, "creare un test di validazione aggiunge un file Test nel workspace");
            SalvaCr(window, editor);
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").TestValidazione.Count == 1,
                "la CR collega il nuovo test di validazione");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TestDiValidazione_CreaDalTemplate_AggiungeUnTestNelWorkspace), ex);
        }
    }

    [Fact]
    public void NuovaCr_IndicatoreModificheNonSalvate_ComparEAlModificareEScompareDopoSalva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToAnomalie().NuovaChangeRequest();
            Assert.False(VisibileById(window, "Cr_ModificheNonSalvate_Text"), "una CR nuova senza modifiche non mostra l'avviso");

            editor.SetTitolo("CR con indicatore");
            Wait.ForCondition(() => VisibileById(window, "Cr_ModificheNonSalvate_Text"),
                "modificando il titolo compare l'avviso di modifiche non salvate");
            ElementFinders.WaitComboBoxById(window, "Cr_Template_Combo").SelectByText(TemplateAlfa);
            editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
            SalvaCr(window, editor);
            Wait.ForCondition(() => !VisibileById(window, "Cr_ModificheNonSalvate_Text"),
                "dopo il salvataggio l'avviso di modifiche non salvate sparisce");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(NuovaCr_IndicatoreModificheNonSalvate_ComparEAlModificareEScompareDopoSalva), ex);
        }
    }

    [Fact]
    public void NuovaCr_RecuperaBozza_VisibileFinoAllaPrimaModifica()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = new MainShellPage(window).GoToAnomalie().NuovaChangeRequest();
            Wait.ForCondition(() => VisibileById(window, "Cr_RecuperaBozza_Button"),
                "una CR nuova senza modifiche offre 'Recupera bozza locale'");

            editor.SetTitolo("Modifica che nasconde il recupero");
            Wait.ForCondition(() => !VisibileById(window, "Cr_RecuperaBozza_Button"),
                "dopo la prima modifica 'Recupera bozza locale' sparisce");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(NuovaCr_RecuperaBozza_VisibileFinoAllaPrimaModifica), ex);
        }
    }
}
