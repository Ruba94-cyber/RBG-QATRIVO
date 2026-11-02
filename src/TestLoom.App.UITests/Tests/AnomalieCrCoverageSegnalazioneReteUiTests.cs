using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>Editor della Segnalazione di rete (SegnalazioneReteEditorView) non ancora guidato dalla UI: validazione
/// dei campi obbligatori, avviso di modifiche non salvate, conferma di abbandono, split per funzionalita' (conferma
/// e annulla), navigazione tra le NC sorelle, controllo duplicati (collega/crea comunque), ticket TICKET/Ticketing
/// (aggiunta e rimozione persistite), Recupera bozza, Prepara ticket TICKET disponibile anche sulle segnalazioni nuove.
/// Gli effetti sono verificati sui file nonconformita/nc_*.json.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoverageSegnalazioneReteUiTests : UiTestBase
{
    /// <summary>NC aperta sulla funzionalita' Climate e sul componente seminata: candidato del controllo duplicati.</summary>
    private const string NcEsistente = "NC-QA-0001";
    /// <summary>Segnalazione completa (salvata) da modificare, ticket e abbandono.</summary>
    private const string NcSegnalazione = "NC-QA-0002";
    private const string FunzionalitaDuplicata = "Climate";
    private const string VinNuovo = "ZFA0000000000QA01";

    public AnomalieCrCoverageSegnalazioneReteUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageSegnalazioneReteUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        state.Master.Funzionalita.Save(new List<Funzionalita>
        {
            new() { Nome = FunzionalitaDuplicata, TutteLeEcu = true, ModificatoDa = "UITests" },
            new() { Nome = "Infotainment", TutteLeEcu = true, ModificatoDa = "UITests" },
            new() { Nome = "Navigazione", TutteLeEcu = true, ModificatoDa = "UITests" },
            // Funzionalita' senza NC esistenti: lo split su queste due non attiva il controllo duplicati.
            new() { Nome = "Radio", TutteLeEcu = true, ModificatoDa = "UITests" },
            new() { Nome = "Telefono", TutteLeEcu = true, ModificatoDa = "UITests" },
        });

        state.NonConformita.Save(new NonConformita
        {
            Id = NcEsistente, DataApertura = DateTime.UtcNow.AddMinutes(-5), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Descrizione = "NC esistente sulla funzionalita' Climate",
            DaSegnalazioneRete = true, EcuSegnalate = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            FunzionalitaSegnalate = new() { FunzionalitaDuplicata },
            NumeriPraticaRete = new() { "PRATICA-ORIG-1" }, VinCliente = new() { "VINORIG0000000001" },
            Licenze = new() { "LIC-ORIG-1" },
        });
        state.NonConformita.Save(new NonConformita
        {
            Id = NcSegnalazione, DataApertura = DateTime.UtcNow.AddMinutes(-2), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Priorita = PrioritaOperativa.Normal,
            Descrizione = "Descrizione dettagliata originale", DescrizioneBreveSegnalata = "Sintesi originale",
            DaSegnalazioneRete = true, EcuSegnalate = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            FunzionalitaSegnalate = new() { "Infotainment" },
            NumeriPraticaRete = new() { "PRATICA-QA-0002" }, VinCliente = new() { "ZFA0000000000QA02" },
            Licenze = new() { "LIC-QA-0002" },
        });
        return workspace;
    }

    private static bool VisibileById(FlaUI.Core.AutomationElements.Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private string PercorsoNc(string id) => new DataPaths(Workspace.WorkspaceDir).NonConformitaFile(id);

    private IReadOnlyList<string> FileNc() =>
        Directory.GetFiles(new DataPaths(Workspace.WorkspaceDir).NonConformitaDir, "nc_*.json")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    private NonConformita LeggiNc(string id) =>
        JsonIo.TryRead<NonConformita>(PercorsoNc(id), out var errore)
        ?? throw new InvalidOperationException($"NC {id} non leggibile da disco: {errore}");

    private SegnalazioneReteEditorPage ApriSegnalazione(string ncId)
    {
        var window = App.WaitForMainWindow();
        var anomalie = new MainShellPage(window).GoToAnomalie();
        anomalie.Cerca(ncId);
        Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, $"la NC {ncId} compare cercandola per Id");
        ElementFinders.WaitButtonById(window, "Anomalie_Riga_ModificaRapida_Button").SafeInvoke();
        var modificaRapida = App.WaitForWindowByContent("Anomalie_NcModificaRapida_ModificaSegnalazione_Button");
        ElementFinders.WaitButtonById(modificaRapida, "Anomalie_NcModificaRapida_ModificaSegnalazione_Button").SafeInvoke();
        return new SegnalazioneReteEditorPage(window);
    }

    private SegnalazioneReteEditorPage NuovaSegnalazione()
    {
        var window = App.WaitForMainWindow();
        return new MainShellPage(window).GoToAnomalie().NuovaSegnalazioneRete();
    }

    /// <summary>Seleziona una funzionalita' dal flyout 'Scegli funzionalita'' (checkbox anonime, come AggiungiEcu).</summary>
    private void AggiungiFunzionalita(string nome)
    {
        var window = App.WaitForMainWindow();
        ElementFinders.WaitButtonById(window, "Segnalazione_AggiungiFunzionalita_Button").SafeInvoke();
        var check = Wait.Until(
            () => window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.CheckBox).And(cf.ByName(nome))),
            _ => true, description: $"CheckBox '{nome}' nel flyout 'Scegli funzionalita''");
        check.AsCheckBox().Toggle();
        ElementFinders.WaitButtonById(window, "MultiSelectPicker_Add_Button").SafeInvoke();
    }

    /// <summary>Compila una segnalazione nuova con tutti i campi obbligatori e un solo componente; le funzionalita' passate
    /// sono aggiunte dal flyout (con due o piu' l'editor eseguira' lo split).</summary>
    private void CompilaSegnalazioneValida(SegnalazioneReteEditorPage editor, string descrizioneBreve, params string[] funzionalita)
    {
        editor.AggiungiNumeroPratica("PRATICA-QA-0001");
        editor.AggiungiVin(VinNuovo);
        editor.AggiungiLicenza("LIC-QA-0001");
        editor.SetDescrizioneBreve(descrizioneBreve);
        editor.SetDescrizione("Descrizione dettagliata della segnalazione QA");
        editor.SetSeverita("Medium");
        editor.SetPriorita("Normal");
        editor.SetStato("InVerifica");
        editor.AggiungiEcu(App, TestWorkspace.DefaultEcuName);
        foreach (var f in funzionalita) AggiungiFunzionalita(f);
    }

    private void ConfermaSi()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma (Si)");
    }

    private void ConfermaNo()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmNo();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura della conferma (No)");
    }

    [Fact]
    public void Segnalazione_SalvaSenzaCampiObbligatori_MostraGliErroriSenzaCreareNc()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var nc = FileNc().Count;
            var editor = NuovaSegnalazione();
            editor.SalvaDaHeader();

            Wait.ForCondition(() => VisibileById(window, "Segnalazione_MessaggioErrore_Text"),
                "Salva con i campi vuoti mostra il banner degli errori");
            var righe = (ElementFinders.WaitById(window, "Segnalazione_MessaggioErrore_Text").Name ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Assert.True(righe.Length >= 3, $"sono elencati piu' campi mancanti ({righe.Length})");
            Assert.True(ElementFinders.ExistsById(window, "Segnalazione_Title_Text"), "resta nell'editor");
            Assert.Equal(nc, FileNc().Count);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_SalvaSenzaCampiObbligatori_MostraGliErroriSenzaCreareNc), ex);
        }
    }

    [Fact]
    public void Segnalazione_ModificaDiUnaSegnalazioneSalvata_MostraLAvvisoEPersisteAlSalvataggio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriSegnalazione(NcSegnalazione);
            Assert.False(VisibileById(window, "Segnalazione_ModificheNonSalvate_Text"), "all'apertura non ci sono modifiche");

            editor.SetDescrizioneBreve("Sintesi modificata QA");
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_ModificheNonSalvate_Text"),
                "modificando un campo compare l'avviso di modifiche non salvate");
            editor.SalvaDaHeader();

            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Salva torna alla lista Anomalie/CR");
            var salvata = LeggiNc(NcSegnalazione);
            Assert.Equal("Sintesi modificata QA", salvata.DescrizioneBreveSegnalata);
            Assert.Contains(salvata.Changelog, c => c.Campo == "segnalazione rete" && c.ValoreNuovo == "modificata");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_ModificaDiUnaSegnalazioneSalvata_MostraLAvvisoEPersisteAlSalvataggio), ex);
        }
    }

    [Fact]
    public void Segnalazione_TornaAllaListaConModificheNonSalvate_ChiedeConfermaENoRestaNellEditor()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriSegnalazione(NcSegnalazione);
            editor.SetDescrizioneBreve("Modifica da abbandonare");

            ElementFinders.WaitButtonById(window, "Segnalazione_TornaAllaLista_Button").SafeInvoke();
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message), "l'abbandono con modifiche chiede conferma");
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "la conferma si chiude dopo 'No'");
            Assert.True(ElementFinders.ExistsById(window, "Segnalazione_Title_Text"), "dopo 'No' resta nell'editor");
            Assert.Equal("Sintesi originale", LeggiNc(NcSegnalazione).DescrizioneBreveSegnalata);

            ElementFinders.WaitButtonById(window, "Segnalazione_TornaAllaLista_Button").SafeInvoke();
            ConfermaSi();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "dopo 'Si' si torna alla lista Anomalie/CR");
            Assert.Equal("Sintesi originale", LeggiNc(NcSegnalazione).DescrizioneBreveSegnalata);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_TornaAllaListaConModificheNonSalvate_ChiedeConfermaENoRestaNellEditor), ex);
        }
    }

    [Fact]
    public void Segnalazione_SplitPerFunzionalita_AnnullaNonCreaNessunaNc()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var nc = FileNc().Count;
            var editor = NuovaSegnalazione();
            CompilaSegnalazioneValida(editor, "Split QA annullato", "Infotainment", "Navigazione");
            editor.SalvaDaHeader();

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message), "con due funzionalita' Salva chiede conferma dello split");
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "la conferma dello split si chiude dopo 'Annulla'");

            Assert.True(ElementFinders.ExistsById(window, "Segnalazione_Title_Text"), "dopo l'annullamento resta nell'editor");
            Assert.Equal(nc, FileNc().Count);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_SplitPerFunzionalita_AnnullaNonCreaNessunaNc), ex);
        }
    }

    [Fact]
    public void Segnalazione_SplitPerFunzionalita_ConfermaCreaDueNcSorelleENavigaTraLeSorelle()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = NuovaSegnalazione();
            CompilaSegnalazioneValida(editor, "Split QA confermato", "Radio", "Telefono");
            editor.SalvaDaHeader();
            ConfermaSi();

            Wait.ForCondition(() => FileNc().Count == 4, "la conferma dello split crea due NC nuove");
            var nuove = FileNc().Select(f => JsonIo.TryRead<NonConformita>(f, out _)!)
                .Where(n => n.GruppoSegnalazioneUid.HasValue && n.Id != NcEsistente && n.Id != NcSegnalazione)
                .OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(2, nuove.Count);
            Assert.Single(nuove.Select(n => n.GruppoSegnalazioneUid).Distinct());
            Assert.Equal(new[] { "Radio", "Telefono" }, nuove.Select(n => n.FunzionalitaSegnalate.Single()).OrderBy(x => x).ToArray());

            var primo = nuove[0].Id;
            var secondo = nuove[1].Id;
            ApriSegnalazione(primo);
            Wait.ForCondition(() => VisibileById(window, $"Segnalazione_Sorella_{secondo}"),
                $"la segnalazione {primo} mostra la sorella {secondo} nel banner");
            ElementFinders.WaitButtonById(window, $"Segnalazione_Sorella_{secondo}").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, $"Segnalazione_Sorella_{primo}"),
                $"aprendo {secondo} il banner propone la sorella {primo}");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_SplitPerFunzionalita_ConfermaCreaDueNcSorelleENavigaTraLeSorelle), ex);
        }
    }

    [Fact]
    public void Segnalazione_Duplicato_CollegaAdNcEsistente_NonCreaUnaNuovaNcEAggiornaLaSorgente()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var nc = FileNc().Count;
            var editor = NuovaSegnalazione();
            CompilaSegnalazioneValida(editor, "Duplicato QA collegato", FunzionalitaDuplicata);
            editor.SalvaDaHeader();

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.Contains(NcEsistente, conferma.Message);
            conferma.ConfirmYes();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "collegando si torna alla lista Anomalie/CR");

            Assert.Equal(nc, FileNc().Count);
            var sorgente = LeggiNc(NcEsistente);
            Assert.Contains(VinNuovo, sorgente.VinCliente);
            Assert.Contains(sorgente.Changelog, c => c.Campo == "segnalazione rete");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_Duplicato_CollegaAdNcEsistente_NonCreaUnaNuovaNcEAggiornaLaSorgente), ex);
        }
    }

    [Fact]
    public void Segnalazione_Duplicato_CreaComunque_CreaUnaNuovaNcSullaFunzionalitaDuplicata()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var nc = FileNc().Count;
            var editor = NuovaSegnalazione();
            CompilaSegnalazioneValida(editor, "Duplicato QA nuovo", FunzionalitaDuplicata);
            editor.SalvaDaHeader();

            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.Contains(NcEsistente, conferma.Message);
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "la conferma si chiude dopo 'Crea comunque'");
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "crea comunque salva e torna alla lista");

            Wait.ForCondition(() => FileNc().Count == nc + 1, "'Crea comunque' crea una NC nuova");
            var nuova = FileNc().Select(f => JsonIo.TryRead<NonConformita>(f, out _)!)
                .Single(n => n.DescrizioneBreveSegnalata == "Duplicato QA nuovo");
            Assert.Equal(new[] { FunzionalitaDuplicata }, nuova.FunzionalitaSegnalate);
            Assert.True(nuova.DaSegnalazioneRete);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_Duplicato_CreaComunque_CreaUnaNuovaNcSullaFunzionalitaDuplicata), ex);
        }
    }

    [Fact]
    public void Segnalazione_TicketAggiungiERimuovi_PersisteNelFileAlSalvataggio()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = ApriSegnalazione(NcSegnalazione);
            ElementFinders.WaitTextBoxById(window, "Segnalazione_NuovoTicket_Riferimento_TextBox").SetText("TICKET-777");
            ElementFinders.WaitTextBoxById(window, "Segnalazione_NuovoTicket_Url_TextBox").SetText("https://ticketing.example/TICKET-777");
            ElementFinders.WaitButtonById(window, "Segnalazione_AggiungiTicket_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Segnalazione_RimuoviTicket_TICKET-777"),
                "il ticket aggiunto compare con il pulsante di rimozione");
            editor.SalvaDaHeader();
            Wait.ForCondition(() => LeggiNc(NcSegnalazione).CollegamentiPolarion.Any(t => t.Codice == "TICKET-777"),
                "il salvataggio scrive il ticket TICKET-777 nel file della NC");

            var riaperto = ApriSegnalazione(NcSegnalazione);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Segnalazione_RimuoviTicket_TICKET-777"),
                "alla riapertura il ticket salvato e' presente");
            ElementFinders.WaitButtonById(window, "Segnalazione_RimuoviTicket_TICKET-777").SafeInvoke();
            Wait.ForCondition(() => !ElementFinders.ExistsById(window, "Segnalazione_RimuoviTicket_TICKET-777"),
                "la rimozione toglie il ticket dall'editor");
            riaperto.SalvaDaHeader();
            Wait.ForCondition(() => !LeggiNc(NcSegnalazione).CollegamentiPolarion.Any(t => t.Codice == "TICKET-777"),
                "la rimozione viene salvata nel file della NC");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_TicketAggiungiERimuovi_PersisteNelFileAlSalvataggio), ex);
        }
    }

    [Fact]
    public void Segnalazione_RecuperaBozza_VisibileFinoAllaPrimaModifica()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var editor = NuovaSegnalazione();
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_RecuperaBozza_Button"),
                "una segnalazione nuova senza modifiche offre 'Recupera bozza locale'");

            editor.SetDescrizioneBreve("Modifica che nasconde il recupero");
            Wait.ForCondition(() => !VisibileById(window, "Segnalazione_RecuperaBozza_Button"),
                "dopo la prima modifica il pulsante 'Recupera bozza locale' sparisce");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_RecuperaBozza_VisibileFinoAllaPrimaModifica), ex);
        }
    }

    [Fact]
    public void Segnalazione_PreparaEsta_AbilitatoAncheSullaSegnalazioneNuova()
    {
        try
        {
            var window = App.WaitForMainWindow();
            NuovaSegnalazione();
            Assert.True(ElementFinders.WaitButtonById(window, "Segnalazione_EsportaPacchetto_Button").IsEnabled,
                "Prepara / Crea ticket TICKET e' disponibile subito, anche su una segnalazione nuova (viene salvata prima)");
            ElementFinders.WaitButtonById(window, "Segnalazione_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "Annulla torna alla lista");

            ApriSegnalazione(NcSegnalazione);
            Wait.ForCondition(() => ElementFinders.WaitButtonById(window, "Segnalazione_EsportaPacchetto_Button").IsEnabled,
                "su una segnalazione salvata Prepara / Crea ticket TICKET e' abilitato");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_PreparaEsta_AbilitatoAncheSullaSegnalazioneNuova), ex);
        }
    }

    [Fact]
    public void Segnalazione_DaApri_SolaLetturaFinoAModifica_ComboSenzaConclusaEAltSinistraTorna()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca(NcSegnalazione);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, $"la NC {NcSegnalazione} compare cercandola per Id");

            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            _ = new SegnalazioneReteEditorPage(window);
            Assert.True(VisibileById(window, "Segnalazione_SolaLettura_Text"),
                "Apri apre la segnalazione di rete in sola lettura");
            Assert.True(VisibileById(window, "Segnalazione_Modifica_Button"), "in sola lettura il pulsante Modifica e' visibile");
            Assert.False(ElementFinders.WaitButtonById(window, "Segnalazione_Salva_Header_Button").IsEnabled,
                "in sola lettura Salva non e' attivo");

            ElementFinders.WaitButtonById(window, "Segnalazione_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => !VisibileById(window, "Segnalazione_SolaLettura_Text"),
                "entrando in modifica l'hint di sola lettura scompare");
            Assert.True(ElementFinders.WaitButtonById(window, "Segnalazione_Salva_Header_Button").IsEnabled,
                "in modifica Salva e' attivo");
            var comboStato = ElementFinders.WaitComboBoxById(window, "Segnalazione_Stato_Combo");
            Wait.ForCondition(() => comboStato.Items.Length > 1, "la combo Stato elenca gli stati disponibili");
            Assert.False(comboStato.Items.Any(item => item.Text == "Conclusa"),
                "una segnalazione aperta non offre Conclusa nella combo Stato");

            AltSinistra(window);
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"),
                "Alt+Sinistra dopo Apri e Modifica torna alla lista");
            Assert.Equal(StatoAnomalia.InVerifica, LeggiNc(NcSegnalazione).StatoWorkflow);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_DaApri_SolaLetturaFinoAModifica_ComboSenzaConclusaEAltSinistraTorna), ex);
        }
    }

    [Fact]
    public void Segnalazione_ChiusuraERiaperturaRichiedonoIlMotivo()
    {
        try
        {
            var window = App.WaitForMainWindow();
            var anomalie = new MainShellPage(window).GoToAnomalie();
            anomalie.Cerca(NcSegnalazione);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, $"la NC {NcSegnalazione} compare cercandola per Id");

            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            _ = new SegnalazioneReteEditorPage(window);
            ElementFinders.WaitButtonById(window, "Segnalazione_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_Chiudi_Button"), "in modifica la NC aperta offre Chiudi");

            // Senza motivo la chiusura e' bloccata con un messaggio e la NC resta aperta.
            ElementFinders.WaitButtonById(window, "Segnalazione_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_MessaggioErrore_Text"),
                "senza motivo la chiusura mostra il messaggio di errore");
            Assert.True(ElementFinders.ExistsById(window, "Segnalazione_Title_Text"), "senza motivo si resta nell'editor");
            Assert.Equal(StatoAnomalia.InVerifica, LeggiNc(NcSegnalazione).StatoWorkflow);

            // Con il motivo la chiusura riesce: si torna alla lista e la NC e' Conclusa.
            ElementFinders.WaitTextBoxById(window, "Segnalazione_MotivoChiusura_TextBox").SetText("Chiusura con motivo QA");
            ElementFinders.WaitButtonById(window, "Segnalazione_Chiudi_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "con il motivo si torna alla lista Anomalie");
            var chiusa = LeggiNc(NcSegnalazione);
            Assert.Equal(StatoAnomalia.Conclusa, chiusa.StatoWorkflow);
            Assert.Equal("Chiusura con motivo QA", chiusa.MotivoChiusura);

            // Riapertura dalla NC chiusa: anche qui il motivo e' obbligatorio.
            anomalie.Cerca(NcSegnalazione);
            ElementFinders.WaitComboBoxById(window, "Anomalie_FiltroStato_Combo").SelectByText("Chiusa");
            Wait.ForCondition(() => anomalie.IdRighe().SequenceEqual(new[] { NcSegnalazione }),
                "il filtro Chiusa mantiene la segnalazione chiusa");
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            _ = new SegnalazioneReteEditorPage(window);
            ElementFinders.WaitButtonById(window, "Segnalazione_Modifica_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_Riapri_Button"), "in modifica la NC chiusa offre Riapri");

            ElementFinders.WaitButtonById(window, "Segnalazione_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => VisibileById(window, "Segnalazione_MessaggioErrore_Text"),
                "senza motivo la riapertura mostra il messaggio di errore");
            Assert.Equal(StatoAnomalia.Conclusa, LeggiNc(NcSegnalazione).StatoWorkflow);

            ElementFinders.WaitTextBoxById(window, "Segnalazione_MotivoRiapertura_TextBox").SetText("Riapertura con motivo QA");
            ElementFinders.WaitButtonById(window, "Segnalazione_Riapri_Button").SafeInvoke();
            Wait.ForCondition(() => ElementFinders.ExistsById(window, "Anomalie_Title_Text"), "con il motivo si torna alla lista Anomalie");
            var riaperta = LeggiNc(NcSegnalazione);
            Assert.Equal(StatoAnomalia.Confermata, riaperta.StatoWorkflow);
            Assert.Null(riaperta.DataChiusura);
            Assert.Empty(riaperta.MotivoChiusura);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Segnalazione_ChiusuraERiaperturaRichiedonoIlMotivo), ex);
        }
    }

    private static void AltSinistra(FlaUI.Core.AutomationElements.Window window)
    {
        ElementFinders.WaitById(window, "Main_Indietro_Button").Focus();
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.ALT, FlaUI.Core.WindowsAPI.VirtualKeyShort.LEFT);
    }
}
