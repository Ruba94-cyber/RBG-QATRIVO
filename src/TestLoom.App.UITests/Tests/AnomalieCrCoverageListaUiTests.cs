using System.Drawing;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Mouse = FlaUI.Core.Input.Mouse;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Flows;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura dei comandi della lista Anomalie/CR non ancora guidati dalla UI (COPERTURA_FLAUI.md, area
/// Anomalie/CR): schede Tutte/Anomalie/Change Request, filtro ticket testo, filtro Stato, filtro Veicolo, viste
/// salvate (salva/applica/elimina con effetto sulla configurazione locale), filtri ticket/veicolo/componente e Assegnati a me
/// della scheda CR, azzeramento dei filtri, etichette azione testuali, apertura editor CR e link ticket non valido.
/// Semina via AppState come le altre suite NC.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoverageListaUiTests : UiTestBase
{
    private const string TemplateCrNome = "CR Template QA";
    private const string NomeVista = "Vista QA ticket 12345";
    /// <summary>Anchor della modifica rapida CR: il selettore del responsabile esiste solo in quella finestra.</summary>
    private const string FinestraRapidaCr = "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox";

    public AnomalieCrCoverageListaUiTests() : base(CreaWorkspace()) { }

    /// <summary>Quattro NC: tre aperte (NC-QA-0001 con TICKET-12345 e veicolo, NC-QA-0002 senza ticket, NC-QA-0004
    /// con ticket TICKET-55555 la cui URL non e' web) e una chiusa (NC-QA-0003). Tre CR: Alfa assegnata, Beta chiusa
    /// e Gamma aperta senza responsabile per coprire le transizioni disponibili e il feedback quando non ce ne sono.</summary>
    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageListaUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var componente = state.Master.Componente.Load();
        componente.Add(new Componente { Nome = "Componente-QA-02", ModificatoDa = "UITests" });
        state.Master.Componente.Save(componente);
        var veicoli = state.Master.Veicoli.Load();
        veicoli.Add(new Veicolo { Nome = "Veicolo-QA-02", ModificatoDa = "UITests" });
        state.Master.Veicoli.Save(veicoli);

        var templates = state.Master.TestTemplate.Load();
        templates.Add(new TestTemplate { Nome = TemplateCrNome, IsChangeRequest = true, ModificatoDa = "UITests" });
        state.Master.TestTemplate.Save(templates);
        var templateCr = templates.Single(t => t.Nome == TemplateCrNome);

        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0001", DataApertura = DateTime.UtcNow.AddMinutes(-4), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Priorita = PrioritaOperativa.Critical,
            Descrizione = "NC con ticket TICKET e veicolo", DaSegnalazioneRete = true,
            VeicoliSegnalati = new() { TestWorkspace.DefaultVehicleName },
            CollegamentiPolarion = { new CollegamentoPolarion { Codice = "TICKET-12345", Url = "https://ticketing.example/TICKET-12345" } },
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0002", DataApertura = DateTime.UtcNow.AddMinutes(-3), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "NC senza ticket",
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0003", DataApertura = DateTime.UtcNow.AddMinutes(-2), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.Conclusa, DataChiusura = DateTime.UtcNow.AddMinutes(-1),
            MotivoChiusura = "Risolta in QA", Descrizione = "NC chiusa",
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = "NC-QA-0004", DataApertura = DateTime.UtcNow.AddMinutes(-1), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "NC con ticket con URL non web",
            CollegamentiPolarion = { new CollegamentoPolarion { Codice = "TICKET-55555", Url = "non-valido" } },
        });

        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QA-0001", Titolo = "CR Alfa di prova", Descrizione = "Descrizione Alfa",
            TemplateUid = templateCr.Uid, Responsabile = TestWorkspace.DefaultOperatorName,
            VeicoliImpattati = new() { TestWorkspace.DefaultVehicleName }, EcuImpattate = new() { TestWorkspace.DefaultEcuName },
            TicketCollegati = new() { new TicketRif { Riferimento = "CR-TICKET-12345", Url = "https://ticketing.example/CR-TICKET-12345" } },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QA-0002", Titolo = "CR Beta di prova", Descrizione = "Descrizione Beta",
            TemplateUid = templateCr.Uid, Responsabile = "Altro QA", Stato = StatoChangeRequest.Chiusa,
            ChiusaIl = DateTime.UtcNow.AddMinutes(-1), ChiusaDa = "QA Master",
            VeicoliImpattati = new() { "Veicolo-QA-02" }, EcuImpattate = new() { "Componente-QA-02" },
        });
        state.ChangeRequests.Save(new ChangeRequest
        {
            Id = "CR-QA-0003", Titolo = "CR Gamma senza responsabile", Descrizione = "CR senza transizione accessibile",
            TemplateUid = templateCr.Uid, Stato = StatoChangeRequest.Aperta,
            VeicoliImpattati = new() { "Veicolo-QA-03" }, EcuImpattate = new() { "Componente-QA-03" },
        });
        return workspace;
    }

    /// <summary>Elemento presente e con area visibile (una sezione nascosta con IsVisible=false non deve contare).</summary>
    private bool VisibileById(Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    /// <summary>Link di un ticket nella colonna Ticket della griglia. La colonna sta oltre la larghezza visibile e il
    /// DataGrid non espone le colonne fuori schermo: si scorre la griglia a destra con il pulsante 'Column right' dello
    /// scorrimento orizzontale (Invoke pattern, nessun clic a coordinate) finche' il link compare nell'albero UIA.</summary>
    private static AutomationElement TrovaLinkTicket(Window window, string codice)
    {
        AutomationElement? link = null;
        Wait.ForCondition(() =>
        {
            link = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName(codice)));
            if (link != null) return true;
            var griglia = ElementFinders.WaitById(window, "Anomalie_Righe_DataGrid");
            var destra = griglia.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button).And(cf.ByName("Column right")));
            if (destra != null && destra.Patterns.Invoke.TryGetPattern(out var invoke)) invoke.Invoke();
            return false;
        }, $"link del ticket {codice} nella colonna Ticket della griglia", TimeSpan.FromSeconds(20));
        return link!;
    }

    /// <summary>Id delle Change Request mostrate nella scheda CR, in ordine verticale.</summary>
    private static IReadOnlyList<string> IdCr(Window window) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Where(t => (t.Name ?? "").StartsWith("CR-", StringComparison.Ordinal) && t.BoundingRectangle.Height > 0)
            .OrderBy(t => t.BoundingRectangle.Top).Select(t => t.Name!).ToList();

    private static IReadOnlyList<AutomationElement> IntestazioniCr(Window window) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.HeaderItem))
            .Where(h => h.BoundingRectangle.Width > 0)
            .OrderBy(h => h.BoundingRectangle.Left)
            // UIA espone la colonna Azioni anche come intestazione duplicata a destra: si contano le colonne per nome, non gli elementi.
            .DistinctBy(h => h.Name).ToList();

    private NonConformita LeggiNc(string id) =>
        JsonIo.TryRead<NonConformita>(new DataPaths(Workspace.WorkspaceDir).NonConformitaFile(id), out var errore)
        ?? throw new InvalidOperationException($"NC {id} non leggibile da disco: {errore}");

    private ChangeRequest LeggiCr(string id) =>
        JsonIo.TryRead<ChangeRequest>(new DataPaths(Workspace.WorkspaceDir).ChangeRequestFile(id), out var errore)
        ?? throw new InvalidOperationException($"CR {id} non leggibile da disco: {errore}");

    /// <summary>Riga della scheda CR che contiene l'Id indicato.</summary>
    private static AutomationElement RigaCr(Window window, string id) =>
        Wait.ForElement(() => ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
                .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
                .FirstOrDefault(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null),
            $"riga CR {id}");

    /// <summary>Riga della scheda CR (Id indicato) che mostra anche il testo cercato, es. un riferimento ticket.</summary>
    private static bool ContieneNellaRiga(Window window, string id, string testo) =>
        ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox")
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
            .Any(r => r.FindFirstDescendant(cf => cf.ByName(id)) != null && r.FindFirstDescendant(cf => cf.ByName(testo)) != null);

    /// <summary>Compila riferimento e URL del ticket nella modifica rapida e preme Aggiungi (senza salvare).</summary>
    private static void AggiungiTicketRapido(Window rapida, string riferimento)
    {
        ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_TicketRif_TextBox").SetText(riferimento);
        ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_TicketUrl_TextBox").SetText($"https://ticketing.example/{riferimento}");
        ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_AggiungiTicket_Button").SafeInvoke();
    }

    /// <summary>Messaggio visibile e non vuoto: un TextBlock nascosto puo' avere ancora il testo, ma l'utente non lo legge.</summary>
    private static bool VisibileConTesto(AutomationElement scope, string automationId)
    {
        var el = ElementFinders.TryFindById(scope, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0
            && !string.IsNullOrWhiteSpace(el.Name);
    }

    private static bool TestoVisibile(FlaUI.Core.AutomationElements.AutomationElement scope, string frammento) =>
        scope.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
            .Any(t => (t.Name ?? "").Contains(frammento, StringComparison.Ordinal) && t.BoundingRectangle.Width > 0 && t.BoundingRectangle.Height > 0);

    [Fact]
    public void SchedeTutteAnomalieChangeRequest_MostranoSoloLaSezioneDelTipoScelto()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();

            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Anomalie_Cr_FiltroTesto_Input"),
                "la scheda Change Request mostra il filtro delle CR");
            Wait.ForCondition(() => !VisibileById(window, "Anomalie_Righe_DataGrid"),
                "la scheda Change Request nasconde la griglia delle NC");

            ElementFinders.WaitButtonById(window, "Anomalie_SchedaAnomalie_Toggle").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Anomalie_Righe_DataGrid"),
                "la scheda Anomalie mostra la griglia delle NC");
            Wait.ForCondition(() => !VisibileById(window, "Anomalie_Cr_FiltroTesto_Input"),
                "la scheda Anomalie nasconde la sezione delle CR");

            ElementFinders.WaitButtonById(window, "Anomalie_SchedaTutte_Toggle").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Anomalie_Righe_DataGrid"), "la scheda Tutte mostra la griglia delle NC");
            // In Compatto (6.6.1) la scheda CR e' comprimibile e parte chiusa: la si apre dalla sua intestazione, come fa un operatore.
            if (!VisibileById(window, "Anomalie_Cr_FiltroTesto_Input"))
                ElementFinders.WaitButtonById(window, "Anomalie_Cr_Toggle").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Anomalie_Righe_DataGrid") && VisibileById(window, "Anomalie_Cr_FiltroTesto_Input"),
                "la scheda Tutte mostra entrambe le sezioni");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedeTutteAnomalieChangeRequest_MostranoSoloLaSezioneDelTipoScelto), ex);
        }
    }

    [Fact]
    public void FiltroTicketTesto_TrovaSoloLeNcConIlCodiceDigitato()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 3,
                "di default (stato Aperta) compaiono le tre NC aperte");

            anomalie.EspandiFiltri();
            ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTicket_Input").SetText("12345");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro ticket testo lascia la sola NC con TICKET-12345");
            Assert.Equal(new[] { "NC-QA-0001" }, anomalie.IdRighe());
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroTicketTesto_TrovaSoloLeNcConIlCodiceDigitato), ex);
        }
    }

    [Fact]
    public void FiltroStato_ChiusaPoiRimuoviFiltri_RiportaTutteLeNc()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 3,
                "di default compaiono le tre NC aperte");

            ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroStato_Combo").SelectByText("Chiusa");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro Stato Chiusa lascia la sola NC chiusa");
            Assert.Equal(new[] { "NC-QA-0003" }, anomalie.IdRighe());

            anomalie.AzzeraFiltri();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 4,
                "Rimuovi filtri riporta tutte e quattro le NC (lo stato torna a 'tutti')");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroStato_ChiusaPoiRimuoviFiltri_RiportaTutteLeNc), ex);
        }
    }

    [Fact]
    public void FiltroVeicolo_SelezionaIlVeicoloEFiltraLeNcCollegate()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 3,
                "di default compaiono le tre NC aperte");

            anomalie.SelezionaVoci(App, "Veicolo", TestWorkspace.DefaultVehicleName);
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro veicolo lascia la sola NC segnalata sul veicolo seminato");
            Assert.Equal(new[] { "NC-QA-0001" }, anomalie.IdRighe());
            Assert.Contains(TestWorkspace.DefaultVehicleName, anomalie.TestoFiltro("Veicolo"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(FiltroVeicolo_SelezionaIlVeicoloEFiltraLeNcCollegate), ex);
        }
    }

    [Fact]
    public void VisteSalvate_SalvaApplicaEElimina_AggiornanoLaConfigurazioneLocale()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.EspandiFiltri();
            ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTicket_Input").SetText("12345");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "il filtro ticket e' attivo prima di salvare la vista");

            ElementFinders.WaitButtonById(window, "Anomalie_SalvaVista_Button").SafeInvoke();
            var dialogo = App.WaitForWindowByContent("TextEdit_Save_Button");
            ElementFinders.WaitTextBoxById(dialogo, "TextEdit_Content_TextBox").SetText(NomeVista);
            ElementFinders.WaitButtonById(dialogo, "TextEdit_Save_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("TextEdit_Save_Button"),
                "il dialogo del nome vista si chiude dopo Salva");
            Wait.ForCondition(() => File.ReadAllText(Workspace.ConfigPath).Contains(NomeVista),
                "la vista salvata finisce nella configurazione locale (config.json)");

            ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTicket_Input").SetText("");
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 3,
                "senza filtro ticket tornano le tre NC aperte");

            ElementFinders.WaitComboBoxById(window, "Anomalie_VisteSalvate_Combo").SelectByText(NomeVista);
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 1,
                "applicare la vista ripristina il filtro ticket salvato");
            Assert.Equal("12345", ElementFinders.WaitTextBoxById(window, "Anomalie_FiltroTicket_Input").Text);

            ElementFinders.WaitButtonById(window, "Anomalie_EliminaVista_Button").SafeInvoke();
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            conferma.ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"),
                "la conferma di eliminazione vista si chiude");
            Wait.ForCondition(() => !File.ReadAllText(Workspace.ConfigPath).Contains(NomeVista),
                "la vista eliminata sparisce dalla configurazione locale (config.json)");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(VisteSalvate_SalvaApplicaEElimina_AggiornanoLaConfigurazioneLocale), ex);
        }
    }

    [Fact]
    public void SchedaChangeRequest_RiepilogoContaPrioritaEGravitaAttive()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.SelezionaVoci(App, "Priorita", "Critical");
            anomalie.SelezionaVoci(App, "Gravita", "Alta");

            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => (RiepilogoFiltriCr(window) ?? string.Empty)
                    .EndsWith("(2)", StringComparison.Ordinal),
                "il riepilogo CR conta priorita' e gravita' selezionate nella vista Anomalie");
            Assert.True(VisibileById(window, "Anomalie_Cr_RimuoviFiltri_Button"),
                "Rimuovi filtri e' disponibile finche' i filtri CR condivisi sono attivi");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_RimuoviFiltri_Button").SafeInvoke();
            Wait.ForCondition(() => !(RiepilogoFiltriCr(window) ?? string.Empty)
                    .EndsWith("(2)", StringComparison.Ordinal),
                "Rimuovi filtri rimuove priorita' e gravita' anche dal riepilogo CR");
            Assert.False(VisibileById(window, "Anomalie_Cr_RimuoviFiltri_Button"));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedaChangeRequest_RiepilogoContaPrioritaEGravitaAttive), ex);
        }
    }

    private static string? RiepilogoFiltriCr(Window window)
    {
        var expander = ElementFinders.TryFindById(window, "Anomalie_Cr_Filtri_Expander");
        return expander == null
            ? null
            : ElementFinders.TryFindById(expander, "ExpanderHeader")?.Name;
    }

    [Fact]
    public void SchedaChangeRequest_FiltroTestoERimuoviFiltri_Agiscono_SoloSulleCr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => IdCr(window).Count == 3, "la scheda CR elenca le tre Change Request seminate");

            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Alfa");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }),
                "il filtro testo della scheda CR lascia solo la CR Alfa");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_RimuoviFiltri_Button").SafeInvoke();
            Wait.ForCondition(() => IdCr(window).Count == 3,
                "Rimuovi filtri della scheda CR riporta entrambe le Change Request");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedaChangeRequest_FiltroTestoERimuoviFiltri_Agiscono_SoloSulleCr), ex);
        }
    }

    [Fact]
    public void SchedaChangeRequest_IntestazioniAllineateELeAzioniRestanoNellaColonna()
    {
        try
        {
            var window = App.WaitForMainWindow();
            FinestraNativa.AbilitaDpiAware();
            FinestraNativa.RidimensionaLogici(window, 1024, 700);
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            var lista = ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox");
            Wait.ForCondition(() => IdCr(window).Count == 3, "le tre Change Request sono visibili per verificare le colonne");

            var allineamenti = new[]
            {
                (Intestazione: "Anomalie_Cr_ColonnaId_Text", Riga: "Anomalie_Cr_RigaId_Text"),
                (Intestazione: "Anomalie_Cr_ColonnaTitolo_Text", Riga: "Anomalie_Cr_RigaTitolo_Text"),
                (Intestazione: "Anomalie_Cr_ColonnaTicket_Text", Riga: "Anomalie_Cr_RigaTicket_Panel"),
                (Intestazione: "Anomalie_Cr_ColonnaStato_Text", Riga: "Anomalie_Cr_RigaStato_Text"),
            };
            foreach (var (idIntestazione, idRiga) in allineamenti)
            {
                var intestazione = ElementFinders.WaitById(window, idIntestazione).BoundingRectangle;
                var contenuto = ElementFinders.WaitById(lista, idRiga).BoundingRectangle;
                Assert.InRange(Math.Abs(intestazione.Left - contenuto.Left), 0, 6);
            }

            Assert.True(ElementFinders.WaitById(lista, "Anomalie_Cr_RigaTitolo_Text").BoundingRectangle.Width >= 120,
                "la colonna Titolo conserva spazio leggibile nella finestra da 1024 px");

            var apriButton = ElementFinders.WaitById(lista, "Anomalie_Cr_Apri_Button");
            var modificaButton = ElementFinders.WaitById(lista, "Anomalie_Cr_ModificaRapida_Button");
            var apri = apriButton.BoundingRectangle;
            var modifica = modificaButton.BoundingRectangle;
            Assert.True(apri.Width > 0 && apri.Height > 0 && modifica.Width > 0 && modifica.Height > 0,
                "i due pulsanti CR hanno un'area visibile nella riga");
            Assert.False(apriButton.Properties.IsOffscreen.ValueOrDefault || modificaButton.Properties.IsOffscreen.ValueOrDefault,
                "i pulsanti CR non sono fuori dallo schermo nella finestra ridotta");
            Assert.True(apri.Right <= modifica.Left,
                "Apri e Modifica rapida sono affiancati senza sovrapporsi");
            var centroApriY = (apri.Top + apri.Bottom) / 2;
            var centroModificaY = (modifica.Top + modifica.Bottom) / 2;
            Assert.InRange(Math.Abs(centroApriY - centroModificaY), 0.0, 4.0);
            Assert.False(VisibileById(window, "VuotoCr"), "il messaggio di lista vuota non copre le righe CR presenti");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedaChangeRequest_IntestazioniAllineateELeAzioniRestanoNellaColonna), ex);
        }
    }

    [Fact]
    public void SchedaChangeRequest_RiordinaColonneEPersisteLaScelta()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => IntestazioniCr(window).Count == 7, "le sette intestazioni CR sono visibili");
            var prima = IntestazioniCr(window);
            var ticket = prima.Single(h => h.Name == "Ticket").BoundingRectangle;
            var id = prima.Single(h => h.Name == "ID").BoundingRectangle;
            var y = (int)((ticket.Top + ticket.Bottom) / 2);
            Mouse.Drag(new Point((int)((ticket.Left + ticket.Right) / 2), y), new Point((int)((id.Left + id.Right) / 2), y));
            Wait.ForCondition(() => IntestazioniCr(window)[0].Name == "Ticket", "Ticket si sposta prima della colonna ID");

            App.Dispose();
            using var app2 = TestLoomApplication.Launch(Workspace);
            var window2 = app2.WaitForMainWindow();
            new MainShellPage(window2).GoToAnomalie();
            ElementFinders.WaitButtonById(window2, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => IntestazioniCr(window2).Count == 7, "le intestazioni CR sono visibili dopo il riavvio");
            Wait.ForCondition(() => IntestazioniCr(window2)[0].Name == "Ticket", "l'ordine CR salvato viene ripristinato");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedaChangeRequest_RiordinaColonneEPersisteLaScelta), ex);
        }
    }

    [Fact]
    public void SchedaChangeRequest_FiltriVeicoloEcuEAssegnatiAMe_FiltranoLeCr()
    {
        try
        {
            var window = App.WaitForMainWindow();
            FinestraNativa.AbilitaDpiAware();
            FinestraNativa.RidimensionaLogici(window, 1024, 700);
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            Wait.ForCondition(() => IdCr(window).Count == 3, "la scheda CR mostra tutte le richieste seminate");

            Assert.False(VisibileById(window, "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton"),
                "il filtro Assegnati a me resta nel pannello avanzato CR quando questo è chiuso");
            var expander = ElementFinders.WaitById(window, "Anomalie_Cr_Filtri_Expander");
            Assert.True(expander.Patterns.ExpandCollapse.TryGetPattern(out var expandCollapse),
                "il pannello dei filtri CR espone ExpandCollapse");
            expandCollapse!.Expand();
            Wait.ForCondition(() => VisibileById(window, "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton")
                && VisibileById(window, "Anomalie_Cr_FiltroVeicolo_Button")
                && VisibileById(window, "Anomalie_Cr_FiltroComponente_Button"),
                "il pannello avanzato CR espone assegnatario, veicolo ed componente");
            var cerca = ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input");
            var scrollFiltri = ElementFinders.WaitById(window, "Anomalie_Cr_Filtri_Scroll");
            var altezzaFinestra = window.BoundingRectangle.Height;
            Assert.True(altezzaFinestra > 0, "la finestra principale espone un'altezza UIA valida");
            Assert.True(cerca.BoundingRectangle.Height <= altezzaFinestra * 0.1,
                $"la ricerca resta una riga compatta rispetto alla finestra anche con i filtri avanzati aperti ({cerca.BoundingRectangle.Height}px su {altezzaFinestra}px)");
            Assert.True(scrollFiltri.BoundingRectangle.Height <= altezzaFinestra * 0.35,
                $"il pannello avanzato è limitato e scorrevole rispetto alla finestra ({scrollFiltri.BoundingRectangle.Height}px su {altezzaFinestra}px)");
            Assert.True(ElementFinders.WaitById(window, "Anomalie_Cr_Lista_ListBox").BoundingRectangle.Height > 0,
                "la lista CR resta visibile mentre i filtri avanzati sono aperti");
            Assert.True(VisibileById(window, "Anomalie_Cr_PaginaSuccessiva_Button"),
                "la paginazione resta visibile e interattiva mentre i filtri avanzati sono aperti");
            cerca.SetText("Alfa");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }),
                "la ricerca resta utilizzabile mentre i filtri avanzati sono aperti");
            ElementFinders.WaitButtonById(window, "Anomalie_Cr_RimuoviFiltri_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name
                    .Contains("3 change request", StringComparison.OrdinalIgnoreCase),
                "dopo la ricerca, rimuovi filtri ripristina le tre CR anche se la griglia ne virtualizza solo le righe visibili");

            ElementFinders.WaitById(window, "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }),
                "Assegnati a me lascia la CR affidata all'operatore attivo");
            ElementFinders.WaitById(window, "Anomalie_Cr_FiltroAssegnatiAMe_ToggleButton").AsToggleButton().Toggle();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name
                    .Contains("3 change request", StringComparison.OrdinalIgnoreCase),
                "disattivando Assegnati a me tornano tutte le CR anche se la griglia ne virtualizza solo le righe visibili");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_FiltroVeicolo_Button").SafeInvoke();
            var filtroVeicolo = new FiltroMultiSelectPage(App);
            filtroVeicolo.SelezionaVoce(TestWorkspace.DefaultVehicleName);
            filtroVeicolo.ChiudiConEscape(TestWorkspace.DefaultVehicleName);
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }),
                "il filtro veicolo lascia la CR Alfa collegata al veicolo scelto");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_RimuoviFiltri_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.WaitById(window, "Anomalie_PaginazioneCr_Text").Name
                    .Contains("3 change request", StringComparison.OrdinalIgnoreCase),
                "Rimuovi filtri azzera il filtro veicolo CR anche se la griglia ne virtualizza solo le righe visibili");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_FiltroComponente_Button").SafeInvoke();
            var filtroEcu = new FiltroMultiSelectPage(App);
            filtroEcu.SelezionaVoce("Componente-QA-02");
            filtroEcu.ChiudiConEscape("Componente-QA-02");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0002" }),
                "il filtro componente lascia la CR Beta collegata al componente scelta");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(SchedaChangeRequest_FiltriVeicoloEcuEAssegnatiAMe_FiltranoLeCr), ex);
        }
    }

    [Fact]
    public void RigaSegnalazione_DaRete_UsaAzioniComuniEMantieneAccessibileLEditorRete()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca("NC-QA-0001");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { "NC-QA-0001" }),
                "la ricerca lascia la sola segnalazione di rete seminata");

            var griglia = ElementFinders.WaitById(window, "Anomalie_Righe_DataGrid");
            var riga = Wait.ForElement(() => griglia.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.DataItem))
                    .FirstOrDefault(item => item.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text))
                        .Any(t => t.Name == "NC-QA-0001")),
                "riga della segnalazione di rete filtrata");
            var dettaglio = Wait.ForElement(() => riga.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Riga_Dettaglio_Button")),
                "azione Dettaglio della NC nella riga");
            var modificaRapida = Wait.ForElement(() => riga.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_Riga_ModificaRapida_Button")),
                "azione Modifica rapida della NC nella riga");

            Assert.True(dettaglio.BoundingRectangle.Width > 0 && modificaRapida.BoundingRectangle.Width > 0,
                "la riga mostra le due azioni comuni Dettaglio e Modifica rapida");
            modificaRapida.AsButton().SafeInvoke();
            var dialogoRapido = App.WaitForWindowByContent("Anomalie_NcModificaRapida_Window");
            Assert.True(ElementFinders.WaitButtonById(dialogoRapido, "Anomalie_NcModificaRapida_ModificaSegnalazione_Button").IsEnabled,
                "l'editor specifico della segnalazione resta raggiungibile dalla modifica rapida");
            ElementFinders.WaitButtonById(dialogoRapido, "Anomalie_NcModificaRapida_Annulla_Button").SafeInvoke();
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaSegnalazione_DaRete_UsaAzioniComuniEMantieneAccessibileLEditorRete), ex);
        }
    }

    [Fact]
    public void Nc_ApriDallaRigaEntraInModificaESalva()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca("NC-QA-0002");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { "NC-QA-0002" }), "la ricerca lascia una sola NC");

            // Una NC non nata da segnalazione di rete: Apri mostra il dettaglio in sola lettura (la NC da segnalazione apre il suo editor).
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var dettaglio = new AnomaliaDettaglioPage(App.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
            Assert.False(ElementFinders.WaitTextBoxById(dettaglio.Window, "AnomaliaDettaglio_Owner_TextBox").IsEnabled,
                "Apri mostra il dettaglio in sola lettura");
            dettaglio.EntraInModifica();
            dettaglio.ImpostaOwner("Operatore QA rapido");
            dettaglio.Salva();
            Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), "chiusura dopo il salvataggio della NC");

            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var riaperta = new AnomaliaDettaglioPage(App.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
            Assert.False(ElementFinders.WaitTextBoxById(riaperta.Window, "AnomaliaDettaglio_Owner_TextBox").IsEnabled,
                "il dettaglio si riapre in consultazione");
            riaperta.EntraInModifica();
            Assert.Equal("Operatore QA rapido", riaperta.Owner);
            riaperta.Annulla();
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Nc_ApriDallaRigaEntraInModificaESalva), ex);
        }
    }

    [Fact]
    public void Nc_ChiudiERiapriDalloElencoConMotivazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca("NC-QA-0001");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { "NC-QA-0001" }), "la ricerca lascia una sola NC");
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button").SafeInvoke();

            var stato = App.WaitForWindowByContent("Anomalie_NcStato_Chiudi_Button");
            Assert.NotNull(stato.FindFirstDescendant(cf => cf.ByAutomationId("Anomalie_NcModificaRapida_Salva_Button")));
            Assert.True(ElementFinders.ExistsById(stato, "Anomalie_NcStato_Motivo_Label"),
                "la motivazione ha un'etichetta visibile oltre al placeholder");
            ElementFinders.WaitTextBoxById(stato, "Anomalie_NcStato_Motivo_TextBox").SetText("Chiusura rapida da elenco QA");
            ElementFinders.WaitButtonById(stato, "Anomalie_NcStato_Chiudi_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_NcStato_Chiudi_Button"), "chiusura del dialog cambio stato NC");

            var salvata = LeggiNc("NC-QA-0001");
            Assert.Equal(StatoAnomalia.Conclusa, salvata.StatoWorkflow);
            Assert.Equal("Chiusura rapida da elenco QA", salvata.MotivoChiusura);

            ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroStato_Combo").SelectByText("Chiusa");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { "NC-QA-0001" }),
                "il filtro Chiusa mantiene la stessa NC disponibile per la riapertura rapida");
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button").SafeInvoke();

            // La riapertura richiede il motivo nel campo dedicato (vedi Nc_RiapriDallaRapidaRichiedeIlMotivo per l'errore senza motivo).
            var statoRiapertura = App.WaitForWindowByContent("Anomalie_NcStato_Riapri_Button");
            Assert.Null(statoRiapertura.FindFirstDescendant(cf => cf.ByAutomationId(AnomaliaDettaglioPage.SalvaId)));
            ElementFinders.WaitTextBoxById(statoRiapertura, "Anomalie_Nc_ModificaRapida_MotivoRiapertura_TextBox").SetText("Riapertura rapida da elenco QA");
            ElementFinders.WaitButtonById(statoRiapertura, "Anomalie_NcStato_Riapri_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_NcStato_Riapri_Button"),
                "chiusura del dialog cambio stato dopo la riapertura rapida");

            var riaperta = LeggiNc("NC-QA-0001");
            Assert.Equal(StatoAnomalia.Confermata, riaperta.StatoWorkflow);
            Assert.Null(riaperta.DataChiusura);
            Assert.Empty(riaperta.MotivoChiusura);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Nc_ChiudiERiapriDalloElencoConMotivazione), ex);
        }
    }

    [Fact]
    public void Nc_RiapriDallaRapidaRichiedeIlMotivo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca("NC-QA-0003");
            ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroStato_Combo").SelectByText("Chiusa");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { "NC-QA-0003" }), "la ricerca lascia la NC chiusa");
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button").SafeInvoke();

            var riapertura = App.WaitForWindowByContent("Anomalie_NcStato_Riapri_Button");
            ElementFinders.WaitButtonById(riapertura, "Anomalie_NcStato_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileConTesto(riapertura, "Anomalie_NcModificaRapida_Errore_Text"),
                "senza motivo la riapertura resta bloccata e la modifica rapida mostra il messaggio");
            Assert.Equal(StatoAnomalia.Conclusa, LeggiNc("NC-QA-0003").StatoWorkflow);
            Assert.False(App.IsWindowClosedByContent("Anomalie_NcStato_Riapri_Button"),
                "la modifica rapida resta aperta per inserire il motivo");

            ElementFinders.WaitTextBoxById(riapertura, "Anomalie_Nc_ModificaRapida_MotivoRiapertura_TextBox").SetText("Riapertura con motivo QA");
            ElementFinders.WaitButtonById(riapertura, "Anomalie_NcStato_Riapri_Button").SafeInvoke();
            new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button")).ConfirmYes();
            Wait.ForCondition(() => LeggiNc("NC-QA-0003").StatoWorkflow == StatoAnomalia.Confermata, "con il motivo la NC viene riaperta");
            Wait.ForCondition(() => App.IsWindowClosedByContent("Anomalie_NcStato_Riapri_Button"),
                "la modifica rapida si chiude dopo la riapertura");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Nc_RiapriDallaRapidaRichiedeIlMotivo), ex);
        }
    }

    [Fact]
    public void Cr_ModificaRapidaRiapreUnaRichiestaChiusaConMotivazione()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Beta");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0002" }), "la ricerca lascia la CR chiusa");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button").SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr("CR-QA-0002").Stato);
            Assert.True(VisibileById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button"), "il Master vede Riapri su una CR chiusa");

            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileConTesto(rapida, "Anomalie_Cr_ModificaRapida_Errore_Text"),
                "senza motivazione la riapertura resta bloccata con un messaggio");
            Assert.Equal(StatoChangeRequest.Chiusa, LeggiCr("CR-QA-0002").Stato);

            ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_Motivo_TextBox").SetText("Riapertura dalla lista QA");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => LeggiCr("CR-QA-0002").Stato == StatoChangeRequest.Aperta, "la CR viene riaperta direttamente dalla lista");
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "la modifica rapida si chiude dopo la riapertura");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0002" }), "la CR riaperta resta nella lista filtrata");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Cr_ModificaRapidaRiapreUnaRichiestaChiusaConMotivazione), ex);
        }
    }

    [Fact]
    public void Cr_ModificaRapidaImplementaUnaRichiestaAssegnata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Alfa");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }),
                "la ricerca lascia la CR aperta assegnata all'operatore attivo");

            var iniziale = LeggiCr("CR-QA-0001");
            Assert.Equal(StatoChangeRequest.Aperta, iniziale.Stato);
            Assert.Equal(TestWorkspace.DefaultOperatorName, iniziale.Responsabile);
            Assert.DoesNotContain(iniziale.TestValidazione, link => link.Obbligatorio);

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button").SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.True(VisibileById(rapida, "Anomalie_Cr_ModificaRapida_Implementa_Button"),
                "la CR assegnata all'operatore attivo e' eleggibile all'implementazione rapida");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Implementa_Button").SafeInvoke();

            Wait.ForCondition(() => LeggiCr("CR-QA-0001").Stato == StatoChangeRequest.Implementata,
                "la lista persiste lo stato Implementata senza aprire l'editor completo");
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "la modifica rapida si chiude dopo l'implementazione");
            var implementata = LeggiCr("CR-QA-0001");
            Assert.Equal(TestWorkspace.DefaultOperatorName, implementata.ImplementataDa);
            Assert.NotNull(implementata.ImplementataIl);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Cr_ModificaRapidaImplementaUnaRichiestaAssegnata), ex);
        }
    }

    [Fact]
    public void Cr_SenzaResponsabile_NonOffreImplementaEPermetteDiAssegnare()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Gamma senza responsabile");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0003" }), "la ricerca lascia la CR aperta senza responsabile");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button").SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.False(VisibileById(rapida, "Anomalie_Cr_ModificaRapida_Implementa_Button"),
                "senza responsabile Implementa non è offerta");
            Assert.True(VisibileById(rapida, "Anomalie_Cr_ModificaRapida_Chiudi_Button"),
                "il Master può chiudere una CR aperta con una motivazione");
            Assert.False(VisibileById(rapida, "Anomalie_Cr_ModificaRapida_Riapri_Button"), "una CR aperta non si riapre");
            Assert.True(ElementFinders.WaitComboBoxById(rapida, "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox").IsEnabled,
                "il responsabile si assegna dalla modifica rapida");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "Annulla senza modifiche chiude la finestra");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Cr_SenzaResponsabile_NonOffreImplementaEPermetteDiAssegnare), ex);
        }
    }

    [Fact]
    public void Cr_ChiusuraRapidaRichiedeIlMotivoEPoiLaListaLaMostraChiusa()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Gamma senza responsabile");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0003" }), "la ricerca lascia la CR aperta");

            ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button").SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileConTesto(rapida, "Anomalie_Cr_ModificaRapida_Errore_Text"),
                "senza motivo la chiusura resta bloccata e la finestra mostra il messaggio");
            Assert.Equal(StatoChangeRequest.Aperta, LeggiCr("CR-QA-0003").Stato);
            Assert.False(App.IsWindowClosedByContent(FinestraRapidaCr), "la modifica rapida resta aperta per inserire il motivo");

            ElementFinders.WaitTextBoxById(rapida, "Anomalie_Cr_ModificaRapida_Motivo_TextBox").SetText("Chiusa in QA senza validazione");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "con il motivo la modifica rapida si chiude");

            var chiusa = LeggiCr("CR-QA-0003");
            Assert.Equal(StatoChangeRequest.Chiusa, chiusa.Stato);
            Assert.True(chiusa.ChiusaSenzaValidazione, "una CR non validata viene chiusa senza validazione");
            Wait.ForCondition(() => ElementFinders.WaitByIdWithinRow(RigaCr(window, "CR-QA-0003"), "Anomalie_Cr_RigaStato_Text").Name?.Contains("Chiusa") == true,
                "la lista mostra la CR come Chiusa");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Cr_ChiusuraRapidaRichiedeIlMotivoEPoiLaListaLaMostraChiusa), ex);
        }
    }

    [Fact]
    public void RigaCr_ApriMostraSolaLettura_ModificaRapidaEEditorRestanoDistinti()
    {
        try
        {
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Alfa");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }), "resta solo la CR Alfa");
            Assert.Equal("Ticket", ElementFinders.WaitById(window, "Anomalie_Cr_ColonnaTicket_Text").Name);
            Assert.NotNull(RigaCr(window, "CR-QA-0001").FindFirstDescendant(cf => cf.ByName("CR-TICKET-12345")));

            // Apri porta all'editor della CR in sola lettura (nessuna finestra di dettaglio); Modifica e' disponibile al Master.
            var editor = ChangeRequestEditorFlow.ApriInSolaLettura(window, "CR-QA-0001", "Alfa");
            Assert.False(ElementFinders.WaitTextBoxById(window, "Cr_Titolo_TextBox").IsEnabled, "in sola lettura il titolo non e' modificabile");
            Assert.True(editor.ModificaVisibile, "in sola lettura Modifica e' disponibile al Master");
            editor.TornaAllaLista();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Torna alla lista riporta alla pagina Anomalie");

            // La modifica rapida non ha titolo ne' descrizione (restano nell'editor completo): aggiunge un ticket e salva dalla lista.
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("Alfa");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0001" }), "di nuovo solo la CR Alfa");
            ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button").SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.False(ElementFinders.ExistsById(rapida, "Anomalie_Cr_ModificaRapida_Titolo_TextBox"), "la modifica rapida non ha il titolo");
            Assert.False(ElementFinders.ExistsById(rapida, "Anomalie_Cr_ModificaRapida_Descrizione_TextBox"), "la modifica rapida non ha la descrizione");
            AggiungiTicketRapido(rapida, "TICKET-LISTA-QA-1");
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Salva_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "il salvataggio rapido chiude la finestra");
            Wait.ForCondition(() => ContieneNellaRiga(window, "CR-QA-0001", "TICKET-LISTA-QA-1"), "la lista mostra il ticket aggiunto");
            Assert.Equal("CR Alfa di prova", ElementFinders.WaitByIdWithinRow(RigaCr(window, "CR-QA-0001"), "Anomalie_Cr_RigaTitolo_Text").Name);
            Assert.Equal("CR Alfa di prova", LeggiCr("CR-QA-0001").Titolo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RigaCr_ApriMostraSolaLettura_ModificaRapidaEEditorRestanoDistinti), ex);
        }
    }

    [Fact]
    public void CrAnnullata_ModificaRapidaECambioStatoNonLaRiattivano()
    {
        try
        {
            using (var state = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false))
            {
                var template = state.Master.TestTemplate.Load().Single(t => t.Nome == TemplateCrNome);
                state.ChangeRequests.Save(new ChangeRequest
                {
                    Id = "CR-QA-0004", Titolo = "CR terminale", Descrizione = "Richiesta annullata",
                    TemplateUid = template.Uid, Responsabile = TestWorkspace.DefaultOperatorName,
                    Stato = StatoChangeRequest.Annullata,
                });
            }

            var originale = LeggiCr("CR-QA-0004");
            var revisioniPrima = originale.Revisioni.Count;
            var window = App.WaitForMainWindow();
            new MainShellPage(window).GoToAnomalie();
            ElementFinders.WaitButtonById(window, "Anomalie_SchedaChangeRequest_Toggle").SafeInvoke();
            ElementFinders.WaitTextBoxById(window, "Anomalie_Cr_FiltroTesto_Input").SetText("CR-QA-0004");
            Wait.ForCondition(() => IdCr(window).SequenceEqual(new[] { "CR-QA-0004" }), "la ricerca lascia la CR annullata seminate");

            var modificaRapida = ElementFinders.WaitButtonById(window, "Anomalie_Cr_ModificaRapida_Button");
            Assert.True(modificaRapida.IsEnabled, "la modifica rapida è sempre invocabile, anche per una CR annullata");
            modificaRapida.SafeInvoke();
            var rapida = App.WaitForWindowByContent(FinestraRapidaCr);
            Assert.False(ElementFinders.WaitComboBoxById(rapida, "Anomalie_Cr_ModificaRapida_Responsabile_ComboBox").IsEnabled,
                "la CR annullata non si riassegna dalla modifica rapida");
            Assert.True(TestoVisibile(rapida, "Sola lettura"), "la CR annullata si apre in sola lettura con il testo esplicativo");
            foreach (var id in new[]
            {
                "Anomalie_Cr_ModificaRapida_Implementa_Button",
                "Anomalie_Cr_ModificaRapida_Chiudi_Button",
                "Anomalie_Cr_ModificaRapida_Riapri_Button",
                "Anomalie_Cr_ModificaRapida_Salva_Button",
            })
            {
                Assert.False(VisibileById(rapida, id), $"nessuna azione di scrittura su una CR annullata: {id}");
            }
            ElementFinders.WaitButtonById(rapida, "Anomalie_Cr_ModificaRapida_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent(FinestraRapidaCr), "Annulla chiude la modifica rapida in sola lettura");
            Assert.Equal("CR terminale", LeggiCr("CR-QA-0004").Titolo);

            Assert.Null(ElementFinders.TryFindById(window, "Anomalie_Cr_CambiaStato_Button"));

            var dopo = LeggiCr("CR-QA-0004");
            Assert.Equal(StatoChangeRequest.Annullata, dopo.Stato);
            Assert.Equal("CR terminale", dopo.Titolo);
            Assert.Equal(revisioniPrima, dopo.Revisioni.Count);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(CrAnnullata_ModificaRapidaECambioStatoNonLaRiattivano), ex);
        }
    }

    [Fact]
    public void TicketConUrlNonWeb_ResteraNonCliccabileNellaTabella_MentreQuelloValidoEAttivo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            Wait.ForCondition(() => anomalie.TotaleFiltratoStabile(tentativiMax: 10, pausaMs: 200) == 3,
                "compaiono le tre NC aperte");
            var nonValido = TrovaLinkTicket(window, "TICKET-55555");
            var valido = TrovaLinkTicket(window, "TICKET-12345");

            Assert.False(nonValido.IsEnabled, "un ticket con URL non web non e' cliccabile dalla tabella");
            Assert.True(valido.IsEnabled, "un ticket con URL web valida e' attivo dalla tabella");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(TicketConUrlNonWeb_ResteraNonCliccabileNellaTabella_MentreQuelloValidoEAttivo), ex);
        }
    }
}
