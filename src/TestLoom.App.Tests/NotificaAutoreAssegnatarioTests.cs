using System.Reflection;
using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;

namespace TestLoom.App.Tests;

/// <summary>Feedback utente 6.5.0, punto 6: nessuna notifica di assegnazione quando autore = assegnatario, per
/// tutte le entita' che hanno un assegnatario con notifica (Test e Change Request; Anomalie/NC e Pacchetto non
/// hanno assegnatario). Autore sconosciuto = si notifica (comportamento sicuro).</summary>
[Collection("Localization")]
public sealed class NotificaAutoreAssegnatarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_NotificaAutore_" + Guid.NewGuid().ToString("N"));
    private readonly Func<string, string, string, string, Task<bool>>? _confirmOriginale = ConfirmService.Handler;

    public void Dispose()
    {
        ConfirmService.Handler = _confirmOriginale;
        if (Directory.Exists(_dir)) { try { Directory.Delete(_dir, true); } catch { } }
    }

    [Theory]
    [InlineData("Mario Rossi", "Mario Rossi", false)]
    [InlineData("Mario Rossi", "  mario rossi ", false)]
    [InlineData("Mario Rossi", "Luigi Bianchi", true)]
    [InlineData("", "Luigi Bianchi", true)]
    [InlineData(null, "Luigi Bianchi", true)]
    [InlineData("Mario Rossi", "", false)]
    [InlineData("Mario Rossi", null, false)]
    public void Regola_NotificaSoloSeAutoreDiversoDaAssegnatario(string? autore, string? assegnatario, bool atteso) =>
        Assert.Equal(atteso, NotificaRegole.DeveNotificareAssegnazione(autore, assegnatario));

    private AppState CreaStato(string operatoreAttivo)
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        var ops = state.Master.Operatori.Load().ToList();
        ops.Add(TestOperatori.Crea("Mario Rossi", RuoloOperatore.Master));
        ops.Add(TestOperatori.Crea("Anna Verdi", RuoloOperatore.Master));
        ops.Add(TestOperatori.Crea("Luigi Bianchi", RuoloOperatore.Standard));
        state.Master.Operatori.Save(ops);
        state.ImpostaOperatore(operatoreAttivo, "1234");
        ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
        return state;
    }

    private static async Task<string> CreaTest(AppState state, string assegnatario)
    {
        var vm = new TestCaseEditorViewModel(state, null, () => { }, _ => { });
        vm.SceglieTemplateCommand.Execute(vm.TemplatesDisponibili[0]);
        vm.AggiungiEcuCommand.Execute("BCM");
        vm.VeicoloSelezionato = "Daily 24";
        vm.TestCase.Descrizione = "Descrizione completa";
        vm.AssegnatarioSelezionato = assegnatario;
        vm.StatoLavoroSelezionato = StatoLavoroTest.Pianificato;
        if (vm.RevisioneCorrente != null) vm.RevisioneCorrente.Matricola = "";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Empty(vm.CampiObbligatoriMancanti);
        var id = vm.TestCase.Id;
        vm.Dispose();
        return id;
    }

    private static int Assegnazioni(AppState state, string destinatario) =>
        state.Notifiche.GetAll(destinatario).Count(n => n.Tipo == "Assegnazione");

    [Fact]
    public async Task Test_AutoreIlProprioAssegnatario_NessunaNotifica()
    {
        var state = CreaStato("Mario Rossi");
        await CreaTest(state, "Mario Rossi");
        Assert.Equal(0, Assegnazioni(state, "Mario Rossi"));
    }

    [Fact]
    public async Task Test_AutoreDiversoDaAssegnatario_NotificaAllAssegnatario()
    {
        var state = CreaStato("Mario Rossi");
        await CreaTest(state, "Luigi Bianchi");
        Assert.Equal(1, Assegnazioni(state, "Luigi Bianchi"));
        Assert.Equal(0, Assegnazioni(state, "Mario Rossi"));
    }

    [Fact]
    public async Task Test_MasterSiRiassegnaUnTestEsistente_NessunaNotificaAlMaster_MaAlNuovoAssegnatarioSi()
    {
        var state = CreaStato("Mario Rossi");
        var id = await CreaTest(state, "Luigi Bianchi");

        // Riassegnazione a se stesso: nessuna notifica a Mario.
        var vm = new TestCaseEditorViewModel(state, id, () => { }, _ => { });
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AssegnatarioSelezionato = "Mario Rossi";
        vm.NotaPianificazioneTemp = "Prendo in carico";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Equal("Mario Rossi", state.TestCases.GetById(id)!.Assegnatario);
        Assert.Equal(0, Assegnazioni(state, "Mario Rossi"));

        // Riassegnazione a un altro: la notifica parte.
        vm.IniziaModificaCommand.AttendiModifica();
        vm.AssegnatarioSelezionato = "Anna Verdi";
        vm.NotaPianificazioneTemp = "Passo ad Anna";
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.Equal(1, Assegnazioni(state, "Anna Verdi"));
        vm.Dispose();
    }

    [Fact]
    public void ChangeRequest_AggiornamentiDiStato_NotificanoAncheLAutore_ComportamentoInvariato()
    {
        var state = CreaStato("Mario Rossi");
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { });
        vm.ChangeRequest.Id = "CR-2026-0001";
        vm.ChangeRequest.Responsabile = "Mario Rossi";

        typeof(ChangeRequestEditorViewModel).GetMethod("NotificaInteressati", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { "Aggiornamento" });

        // Scelta prudente: la regola "autore = assegnatario" riguarda SOLO l'evento di assegnazione (Test). Le
        // notifiche di stato/aggiornamento CR restano come prima: l'autore puo' essere destinatario di un'azione richiesta.
        Assert.Contains(state.Notifiche.GetAll("Mario Rossi"), n => n.Tipo == "ChangeRequest");
        Assert.Single(state.Notifiche.GetAll("Anna Verdi"), n => n.Tipo == "ChangeRequest");
    }

    [Fact]
    public void ChangeRequest_ResponsabileDiversoDallAutore_RiceveLaNotifica()
    {
        var state = CreaStato("Mario Rossi");
        var vm = new ChangeRequestEditorViewModel(state, id: null, torna: () => { }, apriTest: _ => { });
        vm.ChangeRequest.Id = "CR-2026-0002";
        vm.ChangeRequest.Responsabile = "Luigi Bianchi";

        typeof(ChangeRequestEditorViewModel).GetMethod("NotificaInteressati", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { "Aggiornamento" });

        Assert.Single(state.Notifiche.GetAll("Luigi Bianchi"), n => n.Tipo == "ChangeRequest");
    }
}
