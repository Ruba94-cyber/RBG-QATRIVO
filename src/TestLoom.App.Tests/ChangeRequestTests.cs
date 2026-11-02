using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Copre il modello CR separato, la convivenza nella pagina Anomalie/CR e il collegamento ai Test
/// veri usati per validare la modifica.</summary>
public sealed class ChangeRequestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_ChangeRequest_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    private (AppState State, TestTemplate TemplateTest, TestTemplate TemplateCr) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir); // Il bootstrap non ricrea una cartella condivisa assente.
        state.InizializzaCartella(_dir);
        var templateTest = state.Master.TestTemplate.Load().First();
        var templateCr = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var tutti = state.Master.TestTemplate.Load().ToList();
        tutti.Add(templateCr);
        state.Master.TestTemplate.Save(tutti);
        return (state, templateTest, templateCr);
    }

    [Fact]
    public void TemplatesDisponibili_NelWizardNormale_EscludeITemplateChangeRequest()
    {
        var (state, templateTest, templateCr) = CreaStato();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Contains(vm.TemplatesDisponibili, t => t.Uid == templateTest.Uid);
        Assert.DoesNotContain(vm.TemplatesDisponibili, t => t.Uid == templateCr.Uid);
    }

    [Fact]
    public void TemplatesDisponibili_InModalitaChangeRequest_MostraSoloITemplateChangeRequest()
    {
        var (state, templateTest, templateCr) = CreaStato();
        var vm = new TestCaseEditorViewModel(state, testCaseId: null, tornaAllaLista: () => { }, navigaATest: _ => { }, modalitaChangeRequest: true);

        Assert.Contains(vm.TemplatesDisponibili, t => t.Uid == templateCr.Uid);
        Assert.DoesNotContain(vm.TemplatesDisponibili, t => t.Uid == templateTest.Uid);
    }

    [Fact]
    public void TemplatesDisponibili_ApertaSuUnaChangeRequestEsistente_RicavaLaModalitaDalTemplateGiaScelto()
    {
        var (state, _, templateCr) = CreaStato();
        var cr = new TestCase { Id = "CR-1", TestTemplateUid = templateCr.Uid, DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(cr, isNew: true);

        // Aperta senza passare modalitaChangeRequest esplicitamente: deve capirlo da sola dal template già scelto.
        var vm = new TestCaseEditorViewModel(state, testCaseId: cr.Id, tornaAllaLista: () => { }, navigaATest: _ => { });

        Assert.Contains(vm.TemplatesDisponibili, t => t.Uid == templateCr.Uid);
    }

    [Fact]
    public void TestCaseListViewModel_EscludeLeChangeRequestDallaLista()
    {
        var (state, templateTest, templateCr) = CreaStato();
        var test = new TestCase { Id = "TC-1", Componente = "BCM", TestTemplateUid = templateTest.Uid, DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        var cr = new ChangeRequest { Id = "CR-2026-0001", TemplateUid = templateCr.Uid, Titolo = "CR separata", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(test, isNew: true);
        state.ChangeRequests.Save(cr);

        var vm = new TestCaseListViewModel(state, _ => { });

        Assert.Contains(vm.Righe, r => r.Id == "TC-1");
        Assert.DoesNotContain(vm.Righe, r => r.Id == "CR-1");
        Assert.DoesNotContain(vm.TemplatesDisponibili, t => t.Uid == templateCr.Uid);
    }

    [Fact]
    public void NonConformitaViewModel_CaricaLeChangeRequestERispettaIlFiltroTipo()
    {
        var (state, templateTest, templateCr) = CreaStato();
        var test = new TestCase { Id = "TC-1", Componente = "BCM", TestTemplateUid = templateTest.Uid, DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        var cr = new ChangeRequest { Id = "CR-2026-0001", TemplateUid = templateCr.Uid, Titolo = "CR separata", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now };
        state.TestCases.Save(test, isNew: true);
        state.ChangeRequests.Save(cr);

        var vm = new NonConformitaViewModel(state, _ => { });

        Assert.Contains(vm.ChangeRequests, tc => tc.Id == "CR-2026-0001");
        Assert.DoesNotContain(vm.ChangeRequests, tc => tc.Id == "TC-1");
        Assert.True(vm.MostraAnomalie);
        Assert.True(vm.MostraChangeRequest);

        vm.FiltroTipo = FiltroTipoAnomaliaOCr.Anomalie;
        Assert.True(vm.MostraAnomalie);
        Assert.False(vm.MostraChangeRequest);

        vm.FiltroTipo = FiltroTipoAnomaliaOCr.ChangeRequest;
        Assert.False(vm.MostraAnomalie);
        Assert.True(vm.MostraChangeRequest);
    }

    [Fact]
    public void NonConformitaViewModel_CreaChangeRequestCommand_InvocaIlCallback()
    {
        var (state, _, _) = CreaStato();
        var invocato = false;
        var vm = new NonConformitaViewModel(state, _ => { }, null, () => invocato = true);

        vm.CreaChangeRequestCommand.Execute(null);

        Assert.True(invocato);
    }

    [Fact]
    public void NonConformitaViewModel_FiltroTicket_TrovaSiaAnomaliaCheChangeRequestConLoStessoTicket()
    {
        var (state, templateTest, templateCr) = CreaStato();
        var cr = new ChangeRequest
        {
            Id = "CR-2026-0001", TemplateUid = templateCr.Uid, Titolo = "CR con ticket", DataCreazione = DateTime.Now, UltimaModifica = DateTime.Now,
            TicketCollegati = new() { new TicketRif { Riferimento = "TICKET-7777" } }
        };
        state.ChangeRequests.Save(cr);

        var vm = new NonConformitaViewModel(state, _ => { });
        vm.FiltroTicket = "TICKET-7777";

        Assert.Contains(vm.ChangeRequests, tc => tc.Id == "CR-2026-0001");
    }

    [Fact]
    public void ChangeRequestRepository_PersisteSeparatamente_MultiVeicolo_EArchivioRipristino()
    {
        var (state, _, templateCr) = CreaStato();
        var cr = new ChangeRequest
        {
            Id = state.ChangeRequests.NextId(new DateTime(2026, 8, 21)), TemplateUid = templateCr.Uid, Titolo = "Aggiorna diagnostica",
            VeicoliImpattati = new() { "Daily 24", "eDaily 24" }, StrumentiImpattati = new() { "ValidationTool" }
        };
        state.ChangeRequests.Save(cr);
        Assert.Null(state.TestCases.GetById(cr.Id));
        Assert.Equal(2, state.ChangeRequests.GetById(cr.Id)!.VeicoliImpattati.Count);
        state.ChangeRequests.Archivia(cr, "Master");
        Assert.Null(state.ChangeRequests.GetAll().FirstOrDefault(c => c.Id == cr.Id));
        state.ChangeRequests.Ripristina(cr, "Master");
        Assert.Contains(state.ChangeRequests.GetAll(), c => c.Id == cr.Id);
    }

    [Fact]
    public void WorkflowCr_ValidaSoloConTestConformeDopoImplementazioneECoperturaCompleta()
    {
        var cr = new ChangeRequest
        {
            Id = "CR-2026-0001", Stato = StatoChangeRequest.DaValidare, ImplementataIl = DateTime.Now.AddMinutes(-10),
            VeicoliImpattati = new() { "Daily 24", "eDaily 24" }, EcuImpattate = new() { "BCM" }, StrumentiImpattati = new() { "ValidationTool" }
        };
        var test = new TestCase
        {
            Id = "TC-VAL-1", Componente = "BCM", Veicoli = new() { "Daily 24", "eDaily 24" }, StrumentiUsati = new() { "ValidationTool" },
            Esecuzioni = new() { new Esecuzione { Iterazione = 1, Data = DateTime.Now, Esito = Esito.Conforme } }
        };
        cr.TestValidazione.Add(new CollegamentoValidazioneCr
        {
            TestId = test.Id, Obbligatorio = true, VeicoliCoperti = new(cr.VeicoliImpattati), EcuCoperte = new() { "BCM" }, StrumentiCoperti = new() { "ValidationTool" }
        });
        var valutazione = ChangeRequestWorkflowService.Valuta(cr, new[] { test }, aggiornaSnapshot: true);
        Assert.True(valutazione.Completa);
        Assert.NotNull(cr.TestValidazione[0].SnapshotValidazione);
        Assert.True(ChangeRequestWorkflowService.RicalcolaStato(cr, new[] { test }));
        Assert.Equal(StatoChangeRequest.Validata, cr.Stato);
    }

    [Fact]
    public void WorkflowCr_NonAccettaEsecuzionePrecedenteAllImplementazione()
    {
        var cr = new ChangeRequest { Id = "CR-1", Stato = StatoChangeRequest.DaValidare, ImplementataIl = DateTime.Now, VeicoliImpattati = new() { "Daily" } };
        var test = new TestCase { Id = "TC-1", Veicoli = new() { "Daily" }, Esecuzioni = new() { new Esecuzione { Data = DateTime.Now.AddMinutes(-1), Esito = Esito.Conforme } } };
        cr.TestValidazione.Add(new CollegamentoValidazioneCr { TestId = test.Id, VeicoliCoperti = new() { "Daily" } });
        var risultato = ChangeRequestWorkflowService.Valuta(cr, new[] { test });
        Assert.False(risultato.Completa);
        Assert.Contains(risultato.Mancanze, m => m.Contains("successiva all'implementazione"));
    }
}
