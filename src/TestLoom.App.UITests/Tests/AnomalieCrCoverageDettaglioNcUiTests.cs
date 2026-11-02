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

/// <summary>Dialogo di dettaglio NC (AnomaliaDettaglioWindow) non ancora guidato dalla UI: campi di workflow
/// (Stato/Severita/Priorita), Destinatario/Causa/StatoDettaglio con campo causa libera, ticket TICKET/Ticketing
/// (formato e URL), collegamento a Test a posteriori, chiusura senza motivazione, chiusura con esiti del Test
/// collegato, eliminazione con conferma (No/Si) fino al ripristino dalla Recovery, Annulla senza salvataggio.
/// Gli effetti sono verificati sul file della NC (nonconformita/nc_*.json) e del Test (testcases/**).</summary>
[Collection("TestLoom UI")]
[Trait("Category", "CRUD")]
public sealed class AnomalieCrCoverageDettaglioNcUiTests : UiTestBase
{
    private const string NcLibera = "NC-QA-0001";
    private const string NcCondizione = "NC-QA-0002";
    private const string TestLibero = "TC-QA-LIBERO";
    private const string TestCondizione = "TC-QA-COND";

    /// <summary>Uid della condizione del test collegato: letto dal test per costruire l'AutomationId del combo esito.</summary>
    private static Guid _uidCondizione;

    /// <summary>Uid della NC libera: dopo l'eliminazione il file resta come lapide logica che lo riporta.</summary>
    private static Guid _uidNcLibera;

    public AnomalieCrCoverageDettaglioNcUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AnomalieCrCoverageDettaglioNcUiTests));
        var configService = new AppConfigService(workspace.ConfigPath);
        using var state = new AppState(configService, runBackgroundMaintenance: false);

        var ncLibera = new NonConformita
        {
            Id = NcLibera, DataApertura = DateTime.UtcNow.AddMinutes(-3), Severita = SeveritaAnomalia.Medium,
            StatoWorkflow = StatoAnomalia.InVerifica, Priorita = PrioritaOperativa.Normal, Owner = "Lucia",
            Descrizione = "NC libera per i campi del dettaglio",
        };
        _uidNcLibera = ncLibera.Uid;
        state.NonConformita.Save(ncLibera);

        state.TestCases.Save(new TestCase
        {
            Id = TestLibero, DescrizioneBreve = "Test collegabile a posteriori", Componente = { TestWorkspace.DefaultEcuName },
            Veicolo = TestWorkspace.DefaultVehicleName, Veicoli = { TestWorkspace.DefaultVehicleName },
            Assegnatario = "UITests", StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddHours(-3),
            UltimaModifica = DateTime.Now.AddHours(-3), ModificatoDa = "UITests",
        }, isNew: true);

        var condizione = new Condizione
        {
            Esito = Esito.NonConforme, Componente = new EcuSelection(new[] { TestWorkspace.DefaultEcuName }),
            Nota = "Operazione con esito da chiudere", DescrizioneProblema = "Problema QA",
        };
        _uidCondizione = condizione.Uid;
        var segnalazione = new SegnalazioneDaClassificare
        {
            TestId = TestCondizione, CondizioneUid = condizione.Uid, EsecuzioneIterazione = 1,
            Stato = StatoSegnalazione.Classificata, AnomaliaUid = Guid.NewGuid(),
        };
        condizione.SegnalazioneUid = segnalazione.Uid;
        var esecuzione = new Esecuzione
        {
            Iterazione = 1, Data = DateTime.Now.AddHours(-3), Operatore = "UITests", Matricola = "VINQA000000000001",
        };
        esecuzione.Condizioni.Add(condizione);
        esecuzione.SincronizzaEsito();
        state.TestCases.Save(new TestCase
        {
            Id = TestCondizione, DescrizioneBreve = "Test con condizione Non Conforme - QA",
            Componente = { TestWorkspace.DefaultEcuName }, Veicolo = TestWorkspace.DefaultVehicleName,
            Veicoli = { TestWorkspace.DefaultVehicleName }, Assegnatario = "UITests",
            StatoLavoro = StatoLavoroTest.InCorso, DataCreazione = DateTime.Now.AddHours(-3),
            UltimaModifica = DateTime.Now.AddHours(-3), ModificatoDa = "UITests",
            Esecuzioni = new List<Esecuzione> { esecuzione },
        }, isNew: true);
        state.Segnalazioni.Save(segnalazione);
        state.NonConformita.Save(new NonConformita
        {
            Uid = segnalazione.AnomaliaUid!.Value, Id = NcCondizione, TestCaseId = TestCondizione,
            EsecuzioneIterazione = 1, DataApertura = DateTime.UtcNow.AddMinutes(-2),
            Descrizione = "NC collegata a un test con esito da chiudere", Severita = SeveritaAnomalia.Medium,
            Priorita = PrioritaOperativa.Normal, Owner = "Mario", Destinatario = DestinatarioSegnalazione.Supplier,
            CausaProbabile = CausaProbabileUdt.Componente, StatoDettaglio = StatoDettaglioNc.InFix,
            StatoWorkflow = StatoAnomalia.Confermata,
            ProveCollegate = new() { new RiferimentoProva { TestId = TestCondizione, EsecuzioneIterazione = 1, CondizioneUid = condizione.Uid } },
        });
        return workspace;
    }

    /// <summary>Apre il dialogo di dettaglio della NC cercandola per Id dalla pagina Anomalie.</summary>
    private AnomaliaDettaglioPage ApriDettaglio(string ncId)
    {
        var window = App.WaitForMainWindow();
        var anomalie = new MainShellPage(window).GoToAnomalie();
        anomalie.Cerca(ncId);
        Wait.ForCondition(() => anomalie.TotaleFiltrato() == 1, $"la NC {ncId} compare cercandola per Id");
        ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
        var dettaglio = new AnomaliaDettaglioPage(App.WaitForWindowByContent(AnomaliaDettaglioPage.SalvaId));
        dettaglio.EntraInModifica();
        return dettaglio;
    }

    private void AttendiChiusuraDialogo(string messaggio) =>
        Wait.ForCondition(() => App.IsWindowClosedByContent(AnomaliaDettaglioPage.SalvaId), messaggio);

    private void ConfermaSi()
    {
        var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
        conferma.ConfirmYes();
        Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "chiusura del dialog di conferma (Si)");
    }

    private static bool VisibileById(Window window, string automationId)
    {
        var el = ElementFinders.TryFindById(window, automationId);
        return el != null && el.BoundingRectangle.Width > 0 && el.BoundingRectangle.Height > 0;
    }

    private string PercorsoNc(string id) => new DataPaths(Workspace.WorkspaceDir).NonConformitaFile(id);

    private NonConformita LeggiNc(string id) =>
        JsonIo.TryRead<NonConformita>(PercorsoNc(id), out var errore)
        ?? throw new InvalidOperationException($"NC {id} non leggibile da disco: {errore}");

    private TestCase LeggiTest(string id)
    {
        var file = Directory.EnumerateFiles(new DataPaths(Workspace.WorkspaceDir).TestCasesDir, $"testcase_{id}.json", SearchOption.AllDirectories).Single();
        return JsonIo.TryRead<TestCase>(file, out var errore)
            ?? throw new InvalidOperationException($"Test {id} non leggibile da disco: {errore}");
    }

    [Fact]
    public void Dettaglio_StatoSeveritaEPriorita_SalvaEPersisteNelFile()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            ElementFinders.WaitComboBoxById(dettaglio.Window, "AnomaliaDettaglio_Stato_Combo").SelectByText("Confermata");
            ElementFinders.WaitComboBoxById(dettaglio.Window, "AnomaliaDettaglio_Severita_Combo").SelectByText("Alta");
            ElementFinders.WaitComboBoxById(dettaglio.Window, "AnomaliaDettaglio_Priorita_Combo").SelectByText("Major");
            dettaglio.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo Salva");

            var nc = LeggiNc(NcLibera);
            Assert.Equal(StatoAnomalia.Confermata, nc.StatoWorkflow);
            Assert.Equal(SeveritaAnomalia.High, nc.Severita);
            Assert.Equal(PrioritaOperativa.Major, nc.Priorita);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_StatoSeveritaEPriorita_SalvaEPersisteNelFile), ex);
        }
    }

    [Fact]
    public void Dettaglio_ConflittoSalvataggio_RicaricaVersioneCorrenteESalvaLaBozzaLocale()
    {
        try
        {
            const string ownerLocale = "Bozza locale QA";
            const string ownerConcorrente = "Versione concorrente QA";
            const string descrizioneConcorrente = "Aggiornata da writer concorrente QA";
            var revisioneBase = LeggiNc(NcLibera).RevisioneDati;
            var dettaglio = ApriDettaglio(NcLibera);
            dettaglio.ImpostaOwner(ownerLocale);

            // Una seconda istanza aggiorna la versione canonica mentre il dettaglio conserva la propria revisione di base.
            using (var writer = new AppState(new AppConfigService(Workspace.ConfigPath), runBackgroundMaintenance: false))
            {
                var ncConcorrente = writer.NonConformita.GetById(NcLibera)!;
                ncConcorrente.Owner = ownerConcorrente;
                ncConcorrente.Descrizione = descrizioneConcorrente;
                writer.NonConformita.Save(ncConcorrente, ncConcorrente.RevisioneDati);
            }

            var dopoWriter = LeggiNc(NcLibera);
            Assert.Equal(ownerConcorrente, dopoWriter.Owner);
            Assert.Equal(descrizioneConcorrente, dopoWriter.Descrizione);
            Assert.True(dopoWriter.RevisioneDati > revisioneBase);

            dettaglio.Salva();
            Wait.ForCondition(() => VisibileById(dettaglio.Window, "AnomaliaDettaglio_Errore_Text"),
                "il salvataggio concorrente mostra il messaggio di conflitto");
            Wait.ForCondition(() => dettaglio.HaRicaricaVersioneCorrente,
                "dopo il conflitto compare 'Ricarica versione corrente'");

            var messaggio = ElementFinders.WaitById(dettaglio.Window, "AnomaliaDettaglio_Errore_Text").Name ?? string.Empty;
            Assert.True(messaggio.Contains("un'altra postazione", StringComparison.OrdinalIgnoreCase),
                "il messaggio segnala che la NC è stata modificata da un'altra postazione");
            Assert.Equal(ownerLocale, dettaglio.Owner);

            var bozza = Assert.Single(FailedSaveDraftInspector.ListPage(
                Workspace.WorkspaceDir, 0, domain: "nonconformita", entityUid: _uidNcLibera));
            Assert.True(bozza.IsReadable, "il conflitto conserva una bozza locale leggibile");
            Assert.Equal(revisioneBase, bozza.ExpectedRevision);

            dettaglio.RicaricaVersioneCorrente();
            Wait.ForCondition(() => !dettaglio.HaRicaricaVersioneCorrente,
                "la scelta di ricaricare chiude lo stato di conflitto");
            Wait.ForCondition(() => dettaglio.Owner == ownerConcorrente,
                "il dettaglio mostra la versione dell'altro operatore");

            var dopoRicarica = LeggiNc(NcLibera);
            Assert.Equal(ownerConcorrente, dopoRicarica.Owner);
            Assert.Equal(descrizioneConcorrente, dopoRicarica.Descrizione);
            Assert.Equal(dopoWriter.RevisioneDati, dopoRicarica.RevisioneDati);

            var bozzaDopoRicarica = Assert.Single(FailedSaveDraftInspector.ListPage(
                Workspace.WorkspaceDir, 0, domain: "nonconformita", entityUid: _uidNcLibera));
            Assert.Equal(bozza.ContentSha256, bozzaDopoRicarica.ContentSha256);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(
                nameof(Dettaglio_ConflittoSalvataggio_RicaricaVersioneCorrenteESalvaLaBozzaLocale), ex);
        }
    }

    [Fact]
    public void Dettaglio_DestinatarioECausaProbabileStatoDettaglio_SalvaEPersisteNelFile()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            ElementFinders.WaitComboBoxById(w, "AnomaliaDettaglio_Destinatario_Combo").SelectByText("Supplier");
            ElementFinders.WaitComboBoxById(w, "AnomaliaDettaglio_CausaProbabile_Combo").SelectByText("UnitLogger");
            ElementFinders.WaitComboBoxById(w, "AnomaliaDettaglio_StatoDettaglio_Combo").SelectByText("In analisi");
            dettaglio.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo Salva");

            var nc = LeggiNc(NcLibera);
            Assert.Equal(DestinatarioSegnalazione.Supplier, nc.Destinatario);
            Assert.Equal(CausaProbabileUdt.UnitLogger, nc.CausaProbabile);
            Assert.Equal(StatoDettaglioNc.InAnalisi, nc.StatoDettaglio);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_DestinatarioECausaProbabileStatoDettaglio_SalvaEPersisteNelFile), ex);
        }
    }

    /// <summary>Scegliendo 'Altro' come causa probabile il campo 'causa libera' compare subito, senza riaprire il dialogo:
    /// ComboBox e visibilita' passano dal ViewModel (CausaProbabileSelezionata / MostraCausaAltro), che notifica il cambio.</summary>
    [Fact]
    public void Dettaglio_CausaProbabileAltro_MostraIlCampoLiberoSubitoDopoLaScelta()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            Assert.False(VisibileById(w, "AnomaliaDettaglio_CausaAltro_TextBox"),
                "il campo causa libera e' nascosto finche' la causa non e' 'Altro'");
            ElementFinders.WaitComboBoxById(w, "AnomaliaDettaglio_CausaProbabile_Combo").SelectByText("Altro");
            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_CausaAltro_TextBox"),
                "scegliendo 'Altro' compare subito il campo causa libera");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_CausaProbabileAltro_MostraIlCampoLiberoSubitoDopoLaScelta), ex);
        }
    }

    [Fact]
    public void Dettaglio_CausaAltro_DopoLaRiaperturaSiCompilaEPersiste()
    {
        try
        {
            var primo = ApriDettaglio(NcLibera);
            ElementFinders.WaitComboBoxById(primo.Window, "AnomaliaDettaglio_CausaProbabile_Combo").SelectByText("Altro");
            primo.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo il salvataggio della causa Altro");
            Assert.Equal(CausaProbabileUdt.Altro, LeggiNc(NcLibera).CausaProbabile);

            var riaperto = ApriDettaglio(NcLibera);
            Wait.ForCondition(() => VisibileById(riaperto.Window, "AnomaliaDettaglio_CausaAltro_TextBox"),
                "alla riapertura con causa 'Altro' il campo causa libera e' visibile");
            ElementFinders.WaitTextBoxById(riaperto.Window, "AnomaliaDettaglio_CausaAltro_TextBox").SetText("Causa QA libera");
            riaperto.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo il salvataggio del testo libero");

            var nc = LeggiNc(NcLibera);
            Assert.Equal(CausaProbabileUdt.Altro, nc.CausaProbabile);
            Assert.Equal("Causa QA libera", nc.CausaProbabileAltro);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_CausaAltro_DopoLaRiaperturaSiCompilaEPersiste), ex);
        }
    }

    [Fact]
    public void Dettaglio_TicketConFormatoNonValido_MostraErroreENonSalvaNulla()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TicketRiferimento_TextBox").SetText("PROVA-1");
            dettaglio.Salva();

            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_Errore_Text"),
                "un riferimento ticket con formato non TICKET mostra un errore");
            Assert.False(string.IsNullOrWhiteSpace(ElementFinders.WaitById(w, "AnomaliaDettaglio_Errore_Text").Name));
            Assert.True(ElementFinders.ExistsById(w, AnomaliaDettaglioPage.SalvaId), "il dialogo resta aperto per correggere");
            Assert.Empty(LeggiNc(NcLibera).CollegamentiPolarion);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_TicketConFormatoNonValido_MostraErroreENonSalvaNulla), ex);
        }
    }

    [Fact]
    public void Dettaglio_TicketConUrlNonValida_MostraErroreENonSalvaNulla()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TicketRiferimento_TextBox").SetText("TICKET-321");
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TicketUrl_TextBox").SetText("non-valido");
            dettaglio.Salva();

            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_Errore_Text"),
                "un link Ticketing che non e' un URL assoluto mostra un errore");
            Assert.True(ElementFinders.ExistsById(w, AnomaliaDettaglioPage.SalvaId), "il dialogo resta aperto");
            Assert.Empty(LeggiNc(NcLibera).CollegamentiPolarion);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_TicketConUrlNonValida_MostraErroreENonSalvaNulla), ex);
        }
    }

    [Fact]
    public void Dettaglio_TicketValido_SalvaEMostraIlBadgeAllaRiapertura()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TicketRiferimento_TextBox").SetText("TICKET-321");
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TicketUrl_TextBox").SetText("https://ticketing.example/TICKET-321");
            dettaglio.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo Salva con ticket valido");

            var salvato = Assert.Single(LeggiNc(NcLibera).CollegamentiPolarion);
            Assert.Equal("TICKET-321", salvato.Codice);
            Assert.Equal("https://ticketing.example/TICKET-321", salvato.Url);

            var riaperto = ApriDettaglio(NcLibera);
            Wait.ForCondition(() => riaperto.Window.FindFirstDescendant(cf => cf.ByName("TICKET-321")) != null,
                "il ticket appena collegato compare come badge alla riapertura");
            riaperto.Annulla();
            AttendiChiusuraDialogo("Annulla chiude il dialogo riaperto");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_TicketValido_SalvaEMostraIlBadgeAllaRiapertura), ex);
        }
    }

    [Fact]
    public void Dettaglio_CollegaTestInesistente_MostraErroreENonCollegaNulla()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_TestIdDaCollegare_TextBox"),
                "una NC senza test mostra il campo 'Collega a Test'");
            ElementFinders.WaitTextBoxById(w, "AnomaliaDettaglio_TestIdDaCollegare_TextBox").SetText("TC-NON-ESISTE");
            dettaglio.Salva();

            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_Errore_Text"),
                "un Test inesistente viene rifiutato con un errore");
            Assert.True(ElementFinders.ExistsById(w, AnomaliaDettaglioPage.SalvaId), "il dialogo resta aperto");
            Assert.Equal(string.Empty, LeggiNc(NcLibera).TestCaseId);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_CollegaTestInesistente_MostraErroreENonCollegaNulla), ex);
        }
    }

    [Fact]
    public void Dettaglio_CollegaTestEsistente_SalvaIlCollegamentoEMostraApriTest()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            ElementFinders.WaitTextBoxById(dettaglio.Window, "AnomaliaDettaglio_TestIdDaCollegare_TextBox").SetText(TestLibero);
            dettaglio.Salva();
            AttendiChiusuraDialogo("il dialogo si chiude dopo il collegamento al Test");

            var nc = LeggiNc(NcLibera);
            Assert.Equal(TestLibero, nc.TestCaseId);
            Assert.Contains(nc.Changelog, c => c.Campo == "collegamento a test" && c.ValoreNuovo == TestLibero);

            var window = App.WaitForMainWindow();
            ElementFinders.WaitButtonById(window, "Anomalie_Riga_Dettaglio_Button").SafeInvoke();
            var finestraDettaglioNc = App.WaitForWindowByContent("Anomalie_Riga_ApriTest_Button");
            Assert.True(ElementFinders.WaitButtonById(finestraDettaglioNc, "Anomalie_Riga_ApriTest_Button").IsEnabled,
                "dopo il collegamento il dettaglio NC espone 'Apri test'");
            new AnomaliaDettaglioPage(finestraDettaglioNc).Annulla();
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_CollegaTestEsistente_SalvaIlCollegamentoEMostraApriTest), ex);
        }
    }

    [Fact]
    public void Dettaglio_ChiudiSenzaMotivazione_MostraErroreENonChiudeLaNc()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var w = dettaglio.Window;
            dettaglio.Chiudi();

            Wait.ForCondition(() => VisibileById(w, "AnomaliaDettaglio_Errore_Text"),
                "senza motivazione la chiusura e' rifiutata con un errore");
            Assert.True(App.IsWindowClosedByContent("Confirm_Yes_Button"), "senza motivazione non compare nessuna conferma");
            Assert.True(ElementFinders.ExistsById(w, AnomaliaDettaglioPage.SalvaId), "il dialogo resta aperto");
            Assert.Equal(StatoAnomalia.InVerifica, LeggiNc(NcLibera).StatoWorkflow);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_ChiudiSenzaMotivazione_MostraErroreENonChiudeLaNc), ex);
        }
    }

    [Fact]
    public void Dettaglio_ChiudiConEsitiDelTestCollegato_AggiornaTestENcConclusa()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcCondizione);
            var w = dettaglio.Window;
            var comboEsito = $"AnomaliaDettaglio_Condizione_{_uidCondizione}_Esito_Combo";
            Wait.ForCondition(() => VisibileById(w, comboEsito),
                "il dialogo mostra l'esito della condizione del Test collegato");

            ElementFinders.WaitComboBoxById(w, comboEsito).SelectByText("Conforme");
            ElementFinders.WaitComboBoxById(w, "AnomaliaDettaglio_Origine_Combo").SelectByText("Rete (problema non nostro)");
            dettaglio.ImpostaMotivoChiusura("Chiusura QA con esito conforme");
            dettaglio.Chiudi();
            ConfermaSi();
            AttendiChiusuraDialogo("il dialogo si chiude dopo la chiusura con esiti");

            var nc = LeggiNc(NcCondizione);
            Assert.Equal(StatoAnomalia.Conclusa, nc.StatoWorkflow);
            Assert.Equal("Chiusura QA con esito conforme", nc.MotivoChiusura);

            // Chiudere con un esito modificato crea la REV n+1 del Test con l'esito scelto (sovrascrittura sulla REV).
            var test = LeggiTest(TestCondizione);
            Assert.Equal(2, test.Esecuzioni.Count);
            Assert.Equal(Esito.Conforme, test.Esecuzioni[^1].Esito);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_ChiudiConEsitiDelTestCollegato_AggiornaTestENcConclusa), ex);
        }
    }

    [Fact]
    public void Dettaglio_EliminaConfermaNo_LaNcRestaSulDisco()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            dettaglio.Elimina();
            var conferma = new ConfirmDialogPage(App.WaitForWindowByContent("Confirm_Yes_Button"));
            Assert.False(string.IsNullOrWhiteSpace(conferma.Message), "la conferma di eliminazione mostra un messaggio");
            conferma.ConfirmNo();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Confirm_Yes_Button"), "la conferma si chiude dopo 'No'");

            Assert.True(ElementFinders.ExistsById(dettaglio.Window, AnomaliaDettaglioPage.SalvaId), "dopo 'No' il dialogo resta aperto");
            Assert.True(File.Exists(PercorsoNc(NcLibera)), "dopo 'No' il file della NC resta su disco");
            dettaglio.Annulla();
            AttendiChiusuraDialogo("Annulla chiude il dialogo");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_EliminaConfermaNo_LaNcRestaSulDisco), ex);
        }
    }

    [Fact]
    public void Dettaglio_EliminaConfermaSi_ArchiviaNelRecuperoERipristinaDaImpostazioni()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            var window = dettaglio.Window;
            dettaglio.Elimina();
            ConfermaSi();
            AttendiChiusuraDialogo("il dialogo si chiude dopo l'eliminazione");

            // Nei domini condivisi l'eliminazione scrive una lapide logica (tombstone) al posto del file: la NC non
            // e' piu' attiva ma il file resta, e la copia e' archiviata nel Recupero (DomainRecoveryService).
            Wait.ForCondition(() => WorkflowTransactionVisibilityService.TryReadTombstoneIdentity(PercorsoNc(NcLibera), out var uidLapide, out _) && uidLapide == _uidNcLibera,
                "la NC eliminata resta nel file solo come lapide logica (nonconformita/)");
            Assert.NotEmpty(DomainRecoveryService.GetAll(new DataPaths(Workspace.WorkspaceDir)));

            var anomalie = new MainShellPage(App.WaitForMainWindow()).GoToAnomalie();
            anomalie.Cerca(NcLibera);
            Wait.ForCondition(() => anomalie.TotaleFiltrato() == 0, "la NC eliminata non compare piu' nella lista");

            var impostazioni = new MainShellPage(App.WaitForMainWindow()).GoToImpostazioni();
            impostazioni.VaiA("recovery");
            var recovery = App.WaitForMainWindow();
            Wait.ForCondition(() => ElementFinders.ExistsById(recovery, "Recovery_Title_Text"), "si apre la pagina Recovery");
            ElementFinders.WaitButtonById(recovery, "Recovery_Ripristina_Button").SafeInvoke();
            ConfermaSi();

            Wait.ForCondition(() => !WorkflowTransactionVisibilityService.TryReadTombstoneIdentity(PercorsoNc(NcLibera), out _, out _) && File.Exists(PercorsoNc(NcLibera)),
                "il ripristino sostituisce la lapide con la NC nel file");
            Assert.Equal(_uidNcLibera, LeggiNc(NcLibera).Uid);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_EliminaConfermaSi_ArchiviaNelRecuperoERipristinaDaImpostazioni), ex);
        }
    }

    [Fact]
    public void Dettaglio_Annulla_ScartaLeModificheSenzaScrivereIlFile()
    {
        try
        {
            var dettaglio = ApriDettaglio(NcLibera);
            dettaglio.ImpostaOwner("Scartato QA");
            dettaglio.Annulla();
            AttendiChiusuraDialogo("Annulla chiude il dialogo senza salvare");

            Assert.Equal("Lucia", LeggiNc(NcLibera).Owner);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Dettaglio_Annulla_ScartaLeModificheSenzaScrivereIlFile), ex);
        }
    }
}
