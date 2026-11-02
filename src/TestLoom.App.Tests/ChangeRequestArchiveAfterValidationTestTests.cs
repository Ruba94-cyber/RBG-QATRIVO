using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>
/// Bug segnalato dall'utente (2026-09-18): "non ho la possibilita di archiviare le CR, quando creo un test
/// collegato alla CR poi lo archivio o simile poi mi da che c'e il file della CR non leggibile". Riproduce
/// esattamente il flusso reale: editor CR -> CreaTestValidazione (Test + CR salvati in una sola
/// WorkflowTransaction, vedi ChangeRequestEditorViewModel.CreaTestValidazione) -> ArchiviaCommand sulla
/// STESSA istanza di editor subito dopo, senza ricaricare la pagina.
/// </summary>
public sealed class ChangeRequestArchiveAfterValidationTestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_CrArchiveAfterTest_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private (AppState State, TestTemplate TemplateCr, TestTemplate TemplateTest) CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var templateCr = new TestTemplate { Nome = "Change Request", IsChangeRequest = true };
        var templateTest = new TestTemplate { Nome = "Test standard" };
        state.Master.TestTemplate.Save(new List<TestTemplate> { templateCr, templateTest });
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "Daily 24", GammaNome = "MY24" } });
        state.Master.Operatori.Save(new List<Operatore> { TestOperatori.Sigilla(new() { Nome = "Operatore Prova A", Ruolo = RuoloOperatore.Standard }) });
        state.Config.OperatoreAttivo = "Operatore Prova A";
        // OperatoreCorrente (da cui PuoScrivere/PuoArchiviare derivano tramite RuoloAttivo) e' rifatto solo
        // dentro RicaricaTutto: senza questa chiamata resterebbe null (l'Operatore non esisteva ancora quando
        // InizializzaCartella ha caricato il workspace la prima volta), e ogni comando protetto da PuoScrivere
        // resterebbe silenziosamente no-op.
        state.RicaricaTutto();
        return (state, templateCr, templateTest);
    }

    [Fact]
    public async Task CreaTestValidazione_PoiArchivia_SubitoDopo_NonLanciaFileNonLeggibile()
    {
        var (state, templateCr, templateTest) = CreaStato();
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { })
        {
            TemplateSelezionato = templateCr,
            TemplateTestDaCreare = templateTest,
            Titolo = "Verifica archiviazione dopo test di validazione",
        };
        vm.AggiungiVeicoliDaGamme(new[] { "MY24" });
        vm.SalvaCommand.Execute(null);
        Assert.Equal("", vm.Errore);
        Assert.False(vm.IsNew);

        vm.CreaTestValidazioneCommand.Execute(null);
        Assert.Equal("", vm.Errore); // Se la transazione fosse fallita l'errore sarebbe qui, non nell'archiviazione.
        Assert.Single(vm.TestValidazione);

        Assert.True(vm.PuoArchiviare, "precondizione del test non soddisfatta: PuoArchiviare e' false.");

        // Nessuna eccezione qui: prima del fix, il file CR poteva risultare "non leggibile"/in conflitto
        // spurio al Save dell'archiviazione perche' letto subito dopo essere stato scritto dentro la
        // transazione precedente. ExecuteAsync, non Execute: il comando e' asincrono (ConfirmService.Chiedi),
        // Execute() lo avvia senza attenderlo e l'asserzione successiva correrebbe in race con l'archiviazione.
        var ex = await Record.ExceptionAsync(() => vm.ArchiviaCommand.ExecuteAsync(null));
        Assert.Null(ex is AggregateException agg ? agg.InnerException : ex);
        Assert.Equal("", vm.Errore);
        Assert.True(vm.ChangeRequest.Archiviata);
    }

    /// <summary>Stesso scenario ma passando esplicitamente dal repository, senza il round-trip async del
    /// RelayCommand: isola se il difetto e' nella visibilita' transazionale del JSON o nella UI del comando.</summary>
    [Fact]
    public void Repository_SalvaConTestValidazioneInTransazione_PoiArchiviaSubitoDopo_NonLanciaFileNonLeggibile()
    {
        var (state, templateCr, _) = CreaStato();
        var cr = new ChangeRequest
        {
            Id = state.ChangeRequests.NextId(),
            Titolo = "CR per verifica archiviazione",
            TemplateUid = templateCr.Uid,
            VeicoliImpattati = new List<string> { "Daily 24" },
            CreatoDa = "Operatore Prova A",
        };
        state.ChangeRequests.Save(cr, null);

        using (var tx = WorkflowTransactionService.Begin(state.Paths, "test:crea-test-validazione"))
        {
            var test = new TestCase
            {
                Id = IdGenerator.Genera(state.TestCases.GetAll(true).Select(t => t.Id), "TC"),
                DataCreazione = DateTime.Now,
                UltimaModifica = DateTime.Now,
                CreatoDa = "Operatore Prova A",
            };
            state.TestCases.Save(test, true);
            cr.TestValidazione.Add(new CollegamentoValidazioneCr { TestId = test.Id, Obbligatorio = true });
            state.ChangeRequests.Save(cr, cr.RevisioneDati);
            tx.Commit();
        }

        var ricaricata = state.ChangeRequests.GetById(cr.Id);
        Assert.NotNull(ricaricata);

        var ex = Record.Exception(() => state.ChangeRequests.Archivia(ricaricata!, "Operatore Prova A"));
        Assert.Null(ex);
    }
}
