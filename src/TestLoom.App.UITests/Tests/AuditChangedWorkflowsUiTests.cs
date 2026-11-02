using System.IO.Compression;
using System.Text;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;
using FlaUI.Core.Definitions;

namespace TestLoom.App.UITests.Tests;

/// <summary>Copertura UI per i flussi Audit introdotti/estesi nel batch corrente: inserimento manuale a riga,
/// conferma di sovrascrittura, ripristino di uno snapshot storico e export limitato al veicolo e al tipo requisito
/// selezionati. Le propagazioni singola e massiva per componente/veicolo sono già esercitate da
/// AuditPropagazioneTests e AuditPropagazioneMassivaTests.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
public sealed class AuditChangedWorkflowsUiTests : UiTestBase
{
    private const string Componente = "Componente-AUD-CHANGED-QA";
    private const string EcuRigaRimozione = "Componente-AUD-ROW-REMOVE-QA";
    private const string Veicolo = "Veicolo-AUD-CHANGED-QA";
    private const string AltroVeicolo = "Veicolo-AUD-CHANGED-ALTRO-QA";
    private const string RequisitoManuale = "REQ-AUD-MANUALE-UI-QA";
    private const string RequisitoManualeCella = "REQ-AUD-MANUALE-CELLA-UI-QA";
    private const string RequisitoLegacyManuale = "REQ-AUD-LEGACY-MANUALE-UI-QA";
    private const string RequisitoRimozioneRiga = "REQ-AUD-RIMOZIONE-RIGA-UI-QA";
    private const string RequisitoRimozioneAltraRiga = "REQ-AUD-RIMOZIONE-ALTRA-RIGA-UI-QA";
    private const string RequisitoStorico = "REQ-AUD-STORICO-UI-QA";
    private const string RequisitoStandard = "REQ-AUD-EXPORT-STANDARD-UI-QA";
    private const string RequisitoD4d = "REQ-AUD-EXPORT-Extra-UI-QA";

    public AuditChangedWorkflowsUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AuditChangedWorkflowsUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Veicoli.Save(new List<Veicolo>
        {
            new() { Nome = Veicolo, ModificatoDa = "UITests" },
            new() { Nome = AltroVeicolo, ModificatoDa = "UITests" },
        });
        state.Master.Componente.Save(new List<Componente>
        {
            new() { Nome = Componente, VeicoliAssociati = { Veicolo, AltroVeicolo }, ModificatoDa = "UITests" },
        });

        var standard = new Requisito { Id = RequisitoStandard, Testo = "Requisito standard esportabile", ModificatoDa = "UITests" };
        var extra = new Requisito { Id = RequisitoD4d, Testo = "Requisito Extra esportabile", ModificatoDa = "UITests" };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, new[] { Veicolo });
        state.Master.Requisiti.Save(new List<Requisito>
        {
            new() { Id = RequisitoManuale, Testo = "Requisito da valutare manualmente", ModificatoDa = "UITests" },
            new() { Id = RequisitoManualeCella, Testo = "Requisito con valutazione manuale per cella", ModificatoDa = "UITests" },
            new() { Id = RequisitoLegacyManuale, Testo = "Requisito con valutazione manuale legacy", ModificatoDa = "UITests" },
            new() { Id = RequisitoStorico, Testo = "Requisito con cronologia", ModificatoDa = "UITests" },
            standard,
            extra,
        });

        var eventId = Guid.NewGuid();
        var now = DateTime.Now;
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(new List<ValutazioneRequisitoEcuVeicolo>
        {
            new()
            {
                RequisitoId = RequisitoStorico,
                Componente = Componente,
                Veicolo = Veicolo,
                Punteggio = PunteggioConformita.ScarsamenteConforme,
                EventoCorrenteId = eventId,
                UltimaModifica = now,
                ModificatoDa = TestWorkspace.DefaultOperatorName,
                Operatore = TestWorkspace.DefaultOperatorName,
                Nota = "Nota corrente prima del ripristino",
                BaselineSw = "SW-CORRENTE",
                Origine = OrigineValutazioneRequisito.Manuale,
                Storico =
                {
                    new SnapshotValutazioneAudit
                    {
                        EventId = Guid.NewGuid(),
                        Origine = OrigineStoricoValutazioneAudit.Manuale,
                        Punteggio = PunteggioConformita.Conforme,
                        Data = now.AddDays(-7),
                        Operatore = TestWorkspace.DefaultOperatorName,
                        Nota = "Nota storica da ripristinare",
                        BaselineSw = "SW-STORICO",
                    },
                },
            },
        });
        state.Master.RisoluzioniConflittoAudit.Save(new List<RisoluzioneConflittoAudit>
        {
            new()
            {
                Componente = Componente,
                RequisitoId = RequisitoLegacyManuale,
                PunteggioManuale = PunteggioConformita.NonConforme,
                Motivazione = "Nota legacy da rimuovere dalla matrice",
                UltimaModifica = DateTime.Now,
                ModificatoDa = TestWorkspace.DefaultOperatorName,
            },
        });
        var testCellaManuale = new TestCase
        {
            Id = "TC-AUD-MANUALE-CELLA-UI-QA",
            Componente = Componente,
            RequisitoId = { RequisitoManualeCella },
            Veicolo = Veicolo,
        };
        testCellaManuale.Esecuzioni.Add(new Esecuzione
        {
            Data = DateTime.Now.AddDays(-1),
            Esito = Esito.Conforme,
            Matricola = "Matricola-AUD-MANUALE-CELLA-QA",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoManualeCella, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(testCellaManuale, true);
        return workspace;
    }

    private AuditPage ApriAudit()
    {
        var window = App.WaitForMainWindow();
        var audit = new MainShellPage(window, App).GoToAudit();
        audit.SelezionaVeicolo(Veicolo);
        Wait.ForCondition(() => audit.TitoloSezioneStandard?.Contains("Standard", StringComparison.Ordinal) == true,
            "matrice Audit pronta sul veicolo del fixture");
        return audit;
    }

    private static List<ValutazioneRequisitoEcuVeicolo> LeggiValutazioni(TestWorkspace workspace)
    {
        var paths = new DataPaths(workspace.WorkspaceDir);
        // In un workspace Phase D il JSON monolitico resta solo la copia di bootstrap; il repository legge
        // invece la testa corrente dal journal/read model, come fa la UI.
        return new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile).Load();
    }

    private static List<RisoluzioneConflittoAudit> LeggiRisoluzioni(TestWorkspace workspace)
    {
        var paths = new DataPaths(workspace.WorkspaceDir);
        return new MasterDataRepository<RisoluzioneConflittoAudit>(paths.RisoluzioniConflittoAuditFile).Load();
    }

    private static List<TestCase> LeggiTestCases(TestWorkspace workspace)
    {
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        return state.TestCases.GetAll().ToList();
    }

    private static void PreparaValutazioniPerRimozioneRiga(TestWorkspace workspace)
    {
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Componente.Save(state.Master.Componente.Load().Append(new Componente
        {
            Nome = EcuRigaRimozione,
            VeicoliAssociati = { Veicolo, AltroVeicolo },
            ModificatoDa = "UITests",
        }).ToList());
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Concat(new[]
        {
            new Requisito { Id = RequisitoRimozioneRiga, Testo = "Requisito con valutazioni da rimuovere", ModificatoDa = "UITests" },
            new Requisito { Id = RequisitoRimozioneAltraRiga, Testo = "Requisito da lasciare intatto", ModificatoDa = "UITests" },
        }).ToList());

        var now = DateTime.Now;
        ValutazioneRequisitoEcuVeicolo Crea(string componente, string requisitoId, string veicolo, PunteggioConformita punteggio,
            OrigineValutazioneRequisito origine, string nota) => new()
        {
            Componente = componente,
            RequisitoId = requisitoId,
            Veicolo = veicolo,
            Punteggio = punteggio,
            Origine = origine,
            Nota = nota,
            BaselineSw = "SW-ROW-REMOVE",
            SorgenteVeicolo = origine == OrigineValutazioneRequisito.Propagata ? AltroVeicolo : "",
            SorgenteTestId = origine == OrigineValutazioneRequisito.Propagata ? "TC-ROW-REMOVE-SOURCE" : "",
            SorgentePunteggio = origine == OrigineValutazioneRequisito.Propagata ? punteggio : null,
            Operatore = TestWorkspace.DefaultOperatorName,
            ModificatoDa = TestWorkspace.DefaultOperatorName,
            UltimaModifica = now,
            EventoCorrenteId = Guid.NewGuid(),
        };

        state.Master.ValutazioniRequisitoEcuVeicolo.Save(state.Master.ValutazioniRequisitoEcuVeicolo.Load().Concat(new[]
        {
            Crea(Componente, RequisitoRimozioneRiga, Veicolo, PunteggioConformita.NonConforme, OrigineValutazioneRequisito.Manuale, "manuale sul veicolo selezionato"),
            Crea(EcuRigaRimozione, RequisitoRimozioneRiga, Veicolo, PunteggioConformita.ModeratamenteConforme, OrigineValutazioneRequisito.Propagata, "propagata sul veicolo selezionato"),
            Crea(Componente, RequisitoRimozioneRiga, AltroVeicolo, PunteggioConformita.Conforme, OrigineValutazioneRequisito.Manuale, "manuale su altro veicolo"),
            Crea(Componente, RequisitoRimozioneAltraRiga, Veicolo, PunteggioConformita.AmpiamenteConforme, OrigineValutazioneRequisito.Manuale, "manuale su altra riga"),
        }).ToList());

        var test = new TestCase
        {
            Id = "TC-AUD-ROW-REMOVE-PRESERVE-QA",
            Componente = Componente,
            RequisitoId = { RequisitoRimozioneRiga },
            Veicolo = Veicolo,
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Data = now.AddDays(-1),
            Esito = Esito.Conforme,
            Matricola = "Matricola-AUD-ROW-REMOVE-QA",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoRimozioneRiga, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(test, true);
    }

    private static string TestoDelloXlsx(string percorso)
    {
        using var archivio = ZipFile.OpenRead(percorso);
        var testo = new StringBuilder();
        foreach (var voce in archivio.Entries)
        {
            using var lettore = new StreamReader(voce.Open());
            testo.Append(lettore.ReadToEnd());
        }
        return testo.ToString();
    }

    [Fact]
    public void Manuale_RigaImpostaValoreEChiedeConfermaPrimaDiSovrascrivere()
    {
        try
        {
            var audit = ApriAudit();
            var window = App.WaitForMainWindow();
            var idRiga = $"Audit_ManualeRiga_Button_{RequisitoManuale}";

            ElementFinders.WaitButtonById(window, idRiga).SafeInvoke();
            var dialogo = App.WaitForWindowByContent("Audit_ManualeBatch_Applica_Button");
            ElementFinders.WaitComboBoxById(dialogo, "Audit_ManualeBatch_Punteggio_ComboBox").SelectByText("Non conforme (0)");
            ElementFinders.WaitTextBoxById(dialogo, "Audit_ManualeBatch_BaselineSw_TextBox").SetText("SW-MANUALE-UI");
            ElementFinders.WaitTextBoxById(dialogo, "Audit_ManualeBatch_Nota_TextBox").SetText("Motivo inserimento manuale UI");
            ElementFinders.WaitButtonById(dialogo, "Audit_ManualeBatch_Applica_Button").SafeInvoke();

            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ManualeBatch_Applica_Button"), "chiusura del dialogo dopo il primo inserimento manuale");
            Wait.ForCondition(() => audit.CellaMostraValutazioneSalvata(Componente, RequisitoManuale),
                "matrice aggiornata con la valutazione manuale sul veicolo selezionato");
            var prima = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo);
            Assert.Equal(PunteggioConformita.NonConforme, prima.Punteggio);
            Assert.Equal("SW-MANUALE-UI", prima.BaselineSw);
            Assert.Equal("Motivo inserimento manuale UI", prima.Nota);
            Assert.False(LeggiValutazioni(Workspace).Any(v => v.Componente == Componente && v.RequisitoId == RequisitoManuale && v.Veicolo == AltroVeicolo),
                "l'inserimento manuale resta limitato al veicolo selezionato");

            ElementFinders.WaitButtonById(window, idRiga).SafeInvoke();
            dialogo = App.WaitForWindowByContent("Audit_ManualeBatch_Applica_Button");
            ElementFinders.WaitComboBoxById(dialogo, "Audit_ManualeBatch_Punteggio_ComboBox").SelectByText("Conforme (5)");
            ElementFinders.WaitTextBoxById(dialogo, "Audit_ManualeBatch_BaselineSw_TextBox").SetText("SW-SOVRASCRITTURA-UI");
            ElementFinders.WaitTextBoxById(dialogo, "Audit_ManualeBatch_Nota_TextBox").SetText("Motivo sovrascrittura UI");
            ElementFinders.WaitButtonById(dialogo, "Audit_ManualeBatch_Applica_Button").SafeInvoke();

            var conferma = ElementFinders.WaitButtonById(dialogo, "Audit_ManualeBatch_ConfermaSovrascrittura_Button");
            Assert.True(conferma.IsEnabled, "la conferma finale di sovrascrittura è disponibile solo dopo aver rivisto il secondo passaggio");
            Assert.Equal(PunteggioConformita.NonConforme,
                Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo).Punteggio);

            conferma.SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ManualeBatch_Applica_Button"), "chiusura del dialogo dopo la conferma della sovrascrittura");
            var dopo = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo);
            Assert.Equal(PunteggioConformita.Conforme, dopo.Punteggio);
            Assert.Equal("Motivo sovrascrittura UI", dopo.Nota);
            Assert.Contains(dopo.Storico, s => s.Punteggio == PunteggioConformita.NonConforme && s.Nota == "Motivo inserimento manuale UI");

            // Anche un valore inserito tramite l'azione a livello requisito si rimuove dalla cella componente×requisito:
            // la rimozione archivia il valore manuale e ne conserva la cronologia.
            audit.AttendiCellaValutazioneSalvata(Componente, RequisitoManuale).SafeInvoke();
            var dettagli = App.WaitForWindowByContent("Audit_ValutazioneSalvata_Rimuovi_Button");
            ElementFinders.WaitButtonById(dettagli, "Audit_ValutazioneSalvata_Rimuovi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ValutazioneSalvata_Rimuovi_Button"), "chiusura del flyout dopo la rimozione del valore manuale della riga");

            var archiviata = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo);
            Assert.True(archiviata.IsArchiviata);
            Assert.Null(archiviata.EventoCorrenteId);
            Assert.Contains(archiviata.Storico, s => s.Punteggio == PunteggioConformita.Conforme && s.Nota == "Motivo sovrascrittura UI");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Manuale_RigaImpostaValoreEChiedeConfermaPrimaDiSovrascrivere), ex);
        }
    }

    [Fact]
    public void ManualeLegacy_ClicSullaCellaMostraERimuoveLaValutazioneConStorico()
    {
        try
        {
            _ = ApriAudit();
            var window = App.WaitForMainWindow();
            ElementFinders.WaitButtonById(window, $"Audit_ManualeLegacy_{Componente}_{RequisitoLegacyManuale}_Button").SafeInvoke();

            var dettagli = App.WaitForWindowByContent("Audit_ManualeLegacy_Rimuovi_Button");
            ElementFinders.WaitButtonById(dettagli, "Audit_ManualeLegacy_Rimuovi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ManualeLegacy_Rimuovi_Button"), "chiusura del flyout dopo la rimozione del valore manuale legacy");

            var legacy = Assert.Single(LeggiRisoluzioni(Workspace), r => r.Componente == Componente && r.RequisitoId == RequisitoLegacyManuale);
            Assert.Null(legacy.PunteggioManuale);
            Assert.Contains(legacy.StoricoPunteggiManuali, s =>
                s.Punteggio == PunteggioConformita.NonConforme && s.Nota == "Nota legacy da rimuovere dalla matrice");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ManualeLegacy_ClicSullaCellaMostraERimuoveLaValutazioneConStorico), ex);
        }
    }

    [Fact]
    public void ManualeVeicoloSuCellaEcuRequisito_RimuoveSoloIlManualeELasciaIlTest()
    {
        try
        {
            var audit = ApriAudit();
            var window = App.WaitForMainWindow();
            var cellaId = $"Audit_Cella_{Componente}_{RequisitoManualeCella}_Element";
            var cella = Helpers.ElementFinders.WaitById(window, cellaId);
            var rettangolo = cella.BoundingRectangle;
            FlaUI.Core.Input.Mouse.RightClick(new System.Drawing.Point(
                rettangolo.Left + rettangolo.Width / 2,
                rettangolo.Top + rettangolo.Height / 2));

            var inserimento = App.WaitForWindowByContent("Audit_ValutazioneVeicolo_Conferma_Button");
            ElementFinders.WaitComboBoxById(inserimento, "Audit_ValutazioneVeicolo_Punteggio_ComboBox").SelectByText("Non conforme (0)");
            ElementFinders.WaitTextBoxById(inserimento, "Audit_ValutazioneVeicolo_Nota_TextBox").SetText("Correzione manuale della cella componente×requisito");
            ElementFinders.WaitButtonById(inserimento, "Audit_ValutazioneVeicolo_Conferma_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ValutazioneVeicolo_Conferma_Button"), "chiusura del flyout di valutazione manuale per cella");

            audit.AttendiCellaValutazioneSalvata(Componente, RequisitoManualeCella).SafeInvoke();
            var dettagli = App.WaitForWindowByContent("Audit_ValutazioneSalvata_Rimuovi_Button");
            ElementFinders.WaitButtonById(dettagli, "Audit_ValutazioneSalvata_Rimuovi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ValutazioneSalvata_Rimuovi_Button"), "chiusura del flyout dopo la rimozione manuale della cella componente×requisito");

            var archiviata = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoManualeCella && v.Veicolo == Veicolo);
            Assert.True(archiviata.IsArchiviata);
            Assert.Contains(archiviata.Storico, s => s.Punteggio == PunteggioConformita.NonConforme && s.Nota == "Correzione manuale della cella componente×requisito");
            var testOriginale = Assert.Single(LeggiTestCases(Workspace), tc => tc.Id == "TC-AUD-MANUALE-CELLA-UI-QA");
            Assert.Single(testOriginale.Esecuzioni);
            Assert.Equal(PunteggioConformita.Conforme, Assert.Single(testOriginale.Esecuzioni.Single().PunteggiRequisiti).Valore);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(ManualeVeicoloSuCellaEcuRequisito_RimuoveSoloIlManualeELasciaIlTest), ex);
        }
    }

    [Fact]
    public void PropagataVeicoloSuCellaEcuRequisito_RimuoveSoloLaCellaEConservaIlTest()
    {
        try
        {
            PreparaValutazioniPerRimozioneRiga(Workspace);
            var testPrima = Assert.Single(LeggiTestCases(Workspace), tc => tc.Id == "TC-AUD-ROW-REMOVE-PRESERVE-QA");
            var fingerprintTest = TestCaseFingerprint.Compute(testPrima);
            var audit = ApriAudit();
            audit.AttendiCellaValutazioneSalvata(EcuRigaRimozione, RequisitoRimozioneRiga).SafeInvoke();

            var dettagli = App.WaitForWindowByContent("Audit_ValutazioneSalvata_Rimuovi_Button");
            ElementFinders.WaitButtonById(dettagli, "Audit_ValutazioneSalvata_Rimuovi_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_ValutazioneSalvata_Rimuovi_Button"),
                "chiusura del flyout dopo la rimozione della valutazione propagata della singola cella componente×requisito");
            Wait.ForCondition(() => LeggiValutazioni(Workspace).Any(v => v.Componente == EcuRigaRimozione &&
                    v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo && v.IsArchiviata),
                "archiviazione della valutazione propagata della cella selezionata");

            var valutazioni = LeggiValutazioni(Workspace);
            var archiviata = Assert.Single(valutazioni,
                v => v.Componente == EcuRigaRimozione && v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo);
            Assert.Equal(OrigineValutazioneRequisito.Propagata, archiviata.Origine);
            Assert.True(archiviata.IsArchiviata);
            Assert.Null(archiviata.EventoCorrenteId);
            Assert.Contains(archiviata.Storico,
                s => s.Punteggio == PunteggioConformita.ModeratamenteConforme && s.Nota == "propagata sul veicolo selezionato");

            var altraCella = Assert.Single(valutazioni,
                v => v.Componente == Componente && v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo);
            Assert.False(altraCella.IsArchiviata);
            Assert.Equal(OrigineValutazioneRequisito.Manuale, altraCella.Origine);
            Assert.Equal("manuale sul veicolo selezionato", altraCella.Nota);
            Assert.Contains(valutazioni, v => v.Componente == Componente && v.RequisitoId == RequisitoRimozioneRiga &&
                v.Veicolo == AltroVeicolo && !v.IsArchiviata);

            var testDopo = Assert.Single(LeggiTestCases(Workspace), tc => tc.Id == "TC-AUD-ROW-REMOVE-PRESERVE-QA");
            Assert.Equal(fingerprintTest, TestCaseFingerprint.Compute(testDopo));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(PropagataVeicoloSuCellaEcuRequisito_RimuoveSoloLaCellaEConservaIlTest), ex);
        }
    }

    [Fact]
    public void RimozioneRiga_AnteprimaConConteggioConfermaESalvaSoloSulVeicoloSelezionato()
    {
        try
        {
            PreparaValutazioniPerRimozioneRiga(Workspace);
            var testPrima = Assert.Single(LeggiTestCases(Workspace), tc => tc.Id == "TC-AUD-ROW-REMOVE-PRESERVE-QA");
            var fingerprintTest = TestCaseFingerprint.Compute(testPrima);
            _ = ApriAudit();
            var window = App.WaitForMainWindow();
            var idAzione = $"Audit_RimuoviRiga_Button_{RequisitoRimozioneRiga}";
            ElementFinders.WaitButtonById(window, idAzione).SafeInvoke();

            var dialogo = App.WaitForWindowByContent("Audit_RimuoviRiga_Conferma_Button");
            var titolo = ElementFinders.WaitById(dialogo, "Audit_RimuoviRiga_Titolo_Text").Name;
            Assert.Contains("2", titolo, StringComparison.Ordinal);
            Assert.Contains(Veicolo, titolo, StringComparison.Ordinal);
            ElementFinders.WaitButtonById(dialogo, "Audit_RimuoviRiga_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_RimuoviRiga_Conferma_Button"), "chiusura dell'anteprima dopo Annulla");
            Assert.Equal(2, LeggiValutazioni(Workspace).Count(v => v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo && !v.IsArchiviata));

            ElementFinders.WaitButtonById(window, idAzione).SafeInvoke();
            dialogo = App.WaitForWindowByContent("Audit_RimuoviRiga_Conferma_Button");
            ElementFinders.WaitButtonById(dialogo, "Audit_RimuoviRiga_Conferma_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_RimuoviRiga_Conferma_Button"), "chiusura dell'anteprima dopo la rimozione confermata");
            Wait.ForCondition(() => LeggiValutazioni(Workspace).Where(v => v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo)
                .All(v => v.IsArchiviata), "archiviazione di entrambe le valutazioni sulla riga del veicolo selezionato");

            var valutazioni = LeggiValutazioni(Workspace);
            Assert.Equal(2, valutazioni.Count(v => v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo && v.IsArchiviata));
            Assert.Contains(valutazioni, v => v.Componente == Componente && v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo &&
                v.Origine == OrigineValutazioneRequisito.Manuale &&
                v.Storico.Any(s => s.Nota == "manuale sul veicolo selezionato"));
            Assert.Contains(valutazioni, v => v.Componente == EcuRigaRimozione && v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == Veicolo &&
                v.Origine == OrigineValutazioneRequisito.Propagata &&
                v.Storico.Any(s => s.Nota == "propagata sul veicolo selezionato"));
            Assert.Contains(valutazioni, v => v.Componente == Componente && v.RequisitoId == RequisitoRimozioneRiga && v.Veicolo == AltroVeicolo && !v.IsArchiviata);
            Assert.Contains(valutazioni, v => v.Componente == Componente && v.RequisitoId == RequisitoRimozioneAltraRiga && v.Veicolo == Veicolo && !v.IsArchiviata);
            var testDopo = Assert.Single(LeggiTestCases(Workspace), tc => tc.Id == "TC-AUD-ROW-REMOVE-PRESERVE-QA");
            Assert.Equal(fingerprintTest, TestCaseFingerprint.Compute(testDopo));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RimozioneRiga_AnteprimaConConteggioConfermaESalvaSoloSulVeicoloSelezionato), ex);
        }
    }

    [Fact]
    public void Storico_RipristinaSnapshotComeNuovaValutazioneSoloDopoConferma()
    {
        try
        {
            var audit = ApriAudit();
            var cella = audit.AttendiCellaValutazioneSalvata(Componente, RequisitoStorico);
            cella.SafeInvoke();
            var dettagli = App.WaitForWindowByContent("Audit_ValutazioneSalvata_Storico_Button");
            ElementFinders.WaitButtonById(dettagli, "Audit_ValutazioneSalvata_Storico_Button").SafeInvoke();
            var storico = App.WaitForWindowByContent("Audit_Storico_Ripristina_Button");

            var sceltaStorica = Wait.ForElement(
                () => storico.FindAllDescendants(cf => cf.ByControlType(ControlType.RadioButton))
                    .FirstOrDefault(r => (r.Name ?? "").Contains("Nota storica da ripristinare", StringComparison.Ordinal) ||
                        r.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                            .Any(t => (t.Name ?? "").Contains("Nota storica da ripristinare", StringComparison.Ordinal))),
                "opzione storica con la nota di fixture");
            sceltaStorica.SafeInvoke();
            ElementFinders.WaitTextBoxById(storico, "Audit_Storico_Nota_TextBox").SetText("Ripristino richiesto da UI");
            ElementFinders.WaitButtonById(storico, "Audit_Storico_Ripristina_Button").SafeInvoke();

            var conferma = ElementFinders.WaitButtonById(storico, "Audit_Storico_ConfermaSovrascrittura_Button");
            Assert.True(conferma.IsEnabled);
            var prima = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoStorico && v.Veicolo == Veicolo);
            Assert.Equal(PunteggioConformita.ScarsamenteConforme, prima.Punteggio);
            Assert.Equal("Nota corrente prima del ripristino", prima.Nota);

            conferma.SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_Storico_Ripristina_Button"), "chiusura dello storico dopo il ripristino confermato");
            var dopo = Assert.Single(LeggiValutazioni(Workspace), v => v.Componente == Componente && v.RequisitoId == RequisitoStorico && v.Veicolo == Veicolo);
            Assert.Equal(PunteggioConformita.Conforme, dopo.Punteggio);
            Assert.Equal("Ripristino richiesto da UI", dopo.Nota);
            Assert.Equal("SW-STORICO", dopo.BaselineSw);
            Assert.Contains(dopo.Storico, s => s.Punteggio == PunteggioConformita.ScarsamenteConforme && s.Nota == "Nota corrente prima del ripristino");
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Storico_RipristinaSnapshotComeNuovaValutazioneSoloDopoConferma), ex);
        }
    }

    [Fact]
    public void Export_FiltroD4dEsportaSoloIlVeicoloSelezionato()
    {
        try
        {
            var audit = ApriAudit();
            var filtro = audit.ApriFiltroTipoRequisito();
            filtro.SelezionaVoce("Extra");
            filtro.ChiudiConEscape("Extra");
            Wait.ForCondition(() => audit.TestoFiltroTipoRequisito == "Extra", "filtro Audit limitato ai requisiti Extra");
            Wait.ForCondition(() => audit.TitoloSezioneStandard == null, "sezione Standard nascosta dal filtro Extra");

            var exportDir = Path.Combine(Workspace.RootDir, "esporti-audit");
            Directory.CreateDirectory(exportDir);
            var percorso = Path.Combine(exportDir, "Audit_Extra_VeicoloSelezionato.xlsx");
            audit.EsportaMatriceExcel().ConfermaPercorso(percorso);
            Wait.ForCondition(() => File.Exists(percorso) && ArchivioZipCompleto(percorso), "file Excel del filtro Audit scritto per intero");

            var testo = TestoDelloXlsx(percorso);
            Assert.Contains(RequisitoD4d, testo);
            Assert.DoesNotContain(RequisitoStandard, testo);
            Assert.Contains(Veicolo, testo);
            Assert.DoesNotContain(AltroVeicolo, testo);
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(Export_FiltroD4dEsportaSoloIlVeicoloSelezionato), ex);
        }
    }

    private static bool ArchivioZipCompleto(string percorso)
    {
        try
        {
            using var archivio = ZipFile.OpenRead(percorso);
            return archivio.Entries.Count > 0;
        }
        catch (IOException) { return false; }
        catch (InvalidDataException) { return false; }
    }
}
