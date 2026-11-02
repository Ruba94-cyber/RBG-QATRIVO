using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.UITests.Helpers;
using TestLoom.App.UITests.Infrastructure;
using TestLoom.App.UITests.PageObjects;

namespace TestLoom.App.UITests.Tests;

/// <summary>FlaUI: anteprima, conteggio e conferma della rimozione Manuale/Propagata a livello colonna componente.</summary>
[Collection("TestLoom UI")]
[Trait("Category", "Audit")]
public sealed class AuditColumnRemovalUiTests : UiTestBase
{
    private const string EcuTarget = "Componente-AUD-COLUMN-REMOVE-QA";
    private const string EcuAltra = "Componente-AUD-COLUMN-REMOVE-OTHER-QA";
    private const string Veicolo = "Veicolo-AUD-COLUMN-REMOVE-QA";
    private const string AltroVeicolo = "Veicolo-AUD-COLUMN-REMOVE-ALTRO-QA";
    private const string RequisitoManuale = "REQ-AUD-COLUMN-REMOVE-MANUAL-QA";
    private const string RequisitoPropagata = "REQ-AUD-COLUMN-REMOVE-PROPAGATED-QA";
    private const string RequisitoArchiviato = "REQ-AUD-COLUMN-REMOVE-ARCHIVED-QA";

    public AuditColumnRemovalUiTests() : base(CreaWorkspace()) { }

    private static TestWorkspace CreaWorkspace()
    {
        var workspace = TestWorkspace.CreateLoggedIn(nameof(AuditColumnRemovalUiTests), seedTestFixtures: false);
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        state.Master.Veicoli.Save(state.Master.Veicoli.Load().Concat(new[]
        {
            new Veicolo { Nome = Veicolo, ModificatoDa = "UITests" },
            new Veicolo { Nome = AltroVeicolo, ModificatoDa = "UITests" },
        }).ToList());
        state.Master.Componente.Save(state.Master.Componente.Load().Concat(new[]
        {
            new Componente { Nome = EcuTarget, VeicoliAssociati = { Veicolo, AltroVeicolo }, ModificatoDa = "UITests" },
            new Componente { Nome = EcuAltra, VeicoliAssociati = { Veicolo }, ModificatoDa = "UITests" },
        }).ToList());

        var requisitoManuale = new Requisito
        {
            Id = RequisitoManuale, Testo = "Valutazione manuale componente", ApplicabileVeicolo = true, ModificatoDa = "UITests",
        };
        var requisitoPropagata = new Requisito
        {
            Id = RequisitoPropagata, Testo = "Valutazione propagata componente", ApplicabileVeicolo = true, ModificatoDa = "UITests",
        };
        var requisitoArchiviato = new Requisito
        {
            Id = RequisitoArchiviato, Testo = "Requisito archiviato", ApplicabileVeicolo = true, Archiviata = true, ModificatoDa = "UITests",
        };
        state.Master.Requisiti.Save(state.Master.Requisiti.Load().Concat(new[]
        {
            requisitoManuale, requisitoPropagata, requisitoArchiviato,
        }).ToList());

        var now = DateTime.Now;
        ValutazioneRequisitoEcuVeicolo Valutazione(string componente, string requisitoId, string veicolo,
            OrigineValutazioneRequisito origine, string nota) => new()
        {
            Componente = componente,
            RequisitoId = requisitoId,
            Veicolo = veicolo,
            Punteggio = origine == OrigineValutazioneRequisito.Propagata
                ? PunteggioConformita.ModeratamenteConforme
                : PunteggioConformita.NonConforme,
            Origine = origine,
            Nota = nota,
            BaselineSw = "SW-COLUMN-REMOVE",
            SorgenteVeicolo = origine == OrigineValutazioneRequisito.Propagata ? AltroVeicolo : "",
            SorgenteTestId = origine == OrigineValutazioneRequisito.Propagata ? "TC-COLUMN-SOURCE" : "",
            SorgentePunteggio = origine == OrigineValutazioneRequisito.Propagata ? PunteggioConformita.ModeratamenteConforme : null,
            Operatore = TestWorkspace.DefaultOperatorName,
            ModificatoDa = TestWorkspace.DefaultOperatorName,
            UltimaModifica = now,
            EventoCorrenteId = Guid.NewGuid(),
        };
        state.Master.ValutazioniRequisitoEcuVeicolo.Save(state.Master.ValutazioniRequisitoEcuVeicolo.Load().Concat(new[]
        {
            Valutazione(EcuTarget, RequisitoManuale, Veicolo, OrigineValutazioneRequisito.Manuale, "manuale target"),
            Valutazione(EcuTarget, RequisitoPropagata, Veicolo, OrigineValutazioneRequisito.Propagata, "propagata target"),
            Valutazione(EcuTarget, RequisitoManuale, AltroVeicolo, OrigineValutazioneRequisito.Manuale, "altro veicolo"),
            Valutazione(EcuAltra, RequisitoManuale, Veicolo, OrigineValutazioneRequisito.Manuale, "altra componente"),
            Valutazione(EcuTarget, RequisitoArchiviato, Veicolo, OrigineValutazioneRequisito.Manuale, "requisito archiviato"),
        }).ToList());
        state.Master.RisoluzioniConflittoAudit.Save(new List<RisoluzioneConflittoAudit>
        {
            new()
            {
                Componente = EcuTarget,
                RequisitoId = RequisitoManuale,
                PunteggioManuale = PunteggioConformita.ScarsamenteConforme,
                Motivazione = "legacy da preservare",
                UltimaModifica = now.AddDays(-1),
                ModificatoDa = TestWorkspace.DefaultOperatorName,
            },
        });
        var test = new TestCase
        {
            Id = "TC-AUD-COLUMN-REMOVE-PRESERVE-QA",
            Componente = EcuTarget,
            RequisitoId = { RequisitoManuale },
            Veicolo = Veicolo,
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Data = now.AddDays(-1),
            Esito = Esito.Conforme,
            Matricola = "Matricola-COLUMN-REMOVE-QA",
            PunteggiRequisiti = { new PunteggioRequisito { RequisitoId = RequisitoManuale, Valore = PunteggioConformita.Conforme } },
        });
        state.TestCases.Save(test, true);
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
        return new MasterDataRepository<ValutazioneRequisitoEcuVeicolo>(paths.ValutazioniRequisitoEcuVeicoloFile).Load();
    }

    private static RisoluzioneConflittoAudit LeggiLegacy(TestWorkspace workspace) =>
        Assert.Single(new MasterDataRepository<RisoluzioneConflittoAudit>(new DataPaths(workspace.WorkspaceDir).RisoluzioniConflittoAuditFile).Load());

    private static TestCase LeggiTest(TestWorkspace workspace)
    {
        using var state = new AppState(new AppConfigService(workspace.ConfigPath), runBackgroundMaintenance: false);
        return Assert.Single(state.TestCases.GetAll(), tc => tc.Id == "TC-AUD-COLUMN-REMOVE-PRESERVE-QA");
    }

    [Fact]
    public void RimozioneColonna_AnteprimaConteggiaEConfermaSoloEcuVeicoloSelezionati()
    {
        try
        {
            var improntaTest = TestCaseFingerprint.Compute(LeggiTest(Workspace));
            var improntaLegacy = EntityStateFingerprint.Compute(LeggiLegacy(Workspace));
            _ = ApriAudit();
            var window = App.WaitForMainWindow();
            ElementFinders.WaitButtonById(window, $"Audit_RimuoviColonna_Button_{EcuTarget}").SafeInvoke();

            var dialogo = App.WaitForWindowByContent("Audit_RimuoviColonna_Conferma_Button");
            var titolo = ElementFinders.WaitById(dialogo, "Audit_RimuoviColonna_Titolo_Text").Name;
            Assert.Contains("2", titolo, StringComparison.Ordinal);
            Assert.Contains(EcuTarget, titolo, StringComparison.Ordinal);
            Assert.Contains(Veicolo, titolo, StringComparison.Ordinal);
            ElementFinders.WaitButtonById(dialogo, "Audit_RimuoviColonna_Annulla_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_RimuoviColonna_Conferma_Button"), "chiusura dell'anteprima dopo Annulla");
            Assert.Equal(3, LeggiValutazioni(Workspace).Count(v => v.Componente == EcuTarget && v.Veicolo == Veicolo && !v.IsArchiviata));

            ElementFinders.WaitButtonById(window, $"Audit_RimuoviColonna_Button_{EcuTarget}").SafeInvoke();
            dialogo = App.WaitForWindowByContent("Audit_RimuoviColonna_Conferma_Button");
            ElementFinders.WaitButtonById(dialogo, "Audit_RimuoviColonna_Conferma_Button").SafeInvoke();
            Wait.ForCondition(() => App.IsWindowClosedByContent("Audit_RimuoviColonna_Conferma_Button"), "chiusura dell'anteprima dopo la rimozione confermata");
            Wait.ForCondition(() => LeggiValutazioni(Workspace)
                .Where(v => v.Componente == EcuTarget && v.Veicolo == Veicolo && v.RequisitoId != RequisitoArchiviato)
                .All(v => v.IsArchiviata), "archiviazione delle valutazioni attive della colonna selezionata");

            var valutazioni = LeggiValutazioni(Workspace);
            Assert.Contains(valutazioni, v => v.Componente == EcuTarget && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo &&
                v.Origine == OrigineValutazioneRequisito.Manuale && v.Storico.Any(s => s.Nota == "manuale target"));
            Assert.Contains(valutazioni, v => v.Componente == EcuTarget && v.RequisitoId == RequisitoPropagata && v.Veicolo == Veicolo &&
                v.Origine == OrigineValutazioneRequisito.Propagata && v.Storico.Any(s => s.Nota == "propagata target"));
            Assert.Contains(valutazioni, v => v.Componente == EcuTarget && v.RequisitoId == RequisitoManuale && v.Veicolo == AltroVeicolo && !v.IsArchiviata);
            Assert.Contains(valutazioni, v => v.Componente == EcuAltra && v.RequisitoId == RequisitoManuale && v.Veicolo == Veicolo && !v.IsArchiviata);
            Assert.Contains(valutazioni, v => v.Componente == EcuTarget && v.RequisitoId == RequisitoArchiviato && v.Veicolo == Veicolo && !v.IsArchiviata);
            Assert.Equal(improntaTest, TestCaseFingerprint.Compute(LeggiTest(Workspace)));
            Assert.Equal(improntaLegacy, EntityStateFingerprint.Compute(LeggiLegacy(Workspace)));
        }
        catch (Exception ex)
        {
            CaptureFailureAndRethrow(nameof(RimozioneColonna_AnteprimaConteggiaEConfermaSoloEcuVeicoloSelezionati), ex);
        }
    }
}
