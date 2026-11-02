using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Dall'editor dei requisiti Extra: aggiunta dei guasti del registro all'elenco del requisito (con le regole dell'editor) e associazione
/// dei Test partendo dal requisito, senza e con requisito già salvato.</summary>
[Collection("Localization")]
public sealed class RequisitiDtcDalRegistroTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_ReqDtcRegistro_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private AppState CreaStato()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_root);
        _stati.Add(state);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM" } });
        var r = new Requisito { Id = "R-Extra", Testo = "Requisito Extra", DtcConteggio = 0 };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        state.Master.Requisiti.Save(new List<Requisito> { r });
        return state;
    }

    private static void SalvaTest(AppState state, string id, string veicolo, string guasto)
    {
        var test = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, ModificatoDa = "Master Prova",
            Veicolo = veicolo, Veicoli = [veicolo], Componente = new EcuSelection(["ECM"])
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = Day, Operatore = "Master Prova", Esito = Esito.Conforme,
            Condizioni = [new Condizione { Guasto = guasto, DtcPasso = PassoDtc.Validazione, DtcVeicolo = veicolo, Esito = Esito.Conforme, Componente = new EcuSelection(["ECM"]) }]
        });
        state.TestCases.Save(test, isNew: true);
    }

    private static async Task ConDialoghi(Func<Task> operazione)
    {
        var selezione = SelezionaDialogService.Handler;
        var conferma = ConfirmService.Handler;
        try
        {
            SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(voci.ToList());
            ConfirmService.Handler = (_, _, _, _) => Task.FromResult(true);
            await operazione();
        }
        finally
        {
            SelezionaDialogService.Handler = selezione;
            ConfirmService.Handler = conferma;
        }
    }

    [Fact]
    public async Task InModifica_AggiungeIDtcDelRegistroEPoiSiAssociaDalRequisito()
    {
        var state = CreaStato();
        SalvaTest(state, "T-1", "V1", "P0100");
        SalvaTest(state, "T-2", "V2", "P0200");

        using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();
        Assert.True(vm.PuoAggiungereDtcDalRegistro);
        Assert.False(vm.PuoAssociareDtcAlRequisito);

        await ConDialoghi(() => vm.AggiungiDtcDalRegistroCommand.ExecuteAsync(null));
        // Il requisito non dichiara sottoveicoli: valgono quelli dell'anagrafica (V1 e V2), quindi entrambi i guasti entrano.
        Assert.Null(vm.MessaggioErrore);
        Assert.Contains("2", vm.Messaggio);
        await vm.SalvaCommand.ExecuteAsync(null);
        Assert.False(vm.InModifica, vm.MessaggioErrore);

        var requisito = state.Master.Requisiti.Load().Single();
        Assert.Equal(new[] { "P0100", "P0200" }, state.Master.RequisitiDtc.Leggi(requisito).Voci.Select(v => v.Codice).OrderBy(c => c));

        // A requisito salvato: il registro mostra le righe come compatibili e l'associazione le collega ai Test.
        vm.ElementoSelezionato = vm.Items.Single();
        Assert.True(vm.PuoAssociareDtcAlRequisito);
        await ConDialoghi(() => vm.AssociaDtcAlRequisitoCommand.ExecuteAsync(null));

        Assert.Contains("Associate 2", vm.Messaggio);
        foreach (var id in new[] { "T-1", "T-2" })
        {
            var corrente = state.TestCases.LoadCurrent(state.TestCases.GetById(id)!)!;
            Assert.Equal(2, corrente.Esecuzioni.Count);
            Assert.Equal("R-Extra", corrente.Esecuzioni[1].Condizioni[0].DtcRequisitoId);
        }
    }

    [Fact]
    public async Task InModifica_UnDtcDiUnVeicoloNonDichiaratoNonEntraEIlMotivoEVisibile()
    {
        var state = CreaStato();
        var r = state.Master.Requisiti.Load().Single();
        RequisitoApplicabilita.ImpostaSottoveicoli(r, new[] { "V2" });
        state.Master.Requisiti.Save(new List<Requisito> { r });
        SalvaTest(state, "T-1", "V1", "P0100");

        using var vm = new RequisitiViewModel(state, TipoRequisito.Extra);
        vm.IniziaModificaCommand.AttendiModifica();
        vm.ElementoSelezionato = vm.Items.Single();

        await ConDialoghi(() => vm.AggiungiDtcDalRegistroCommand.ExecuteAsync(null));

        Assert.Contains(LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"), vm.MessaggioErrore);
        Assert.Contains("0", vm.Messaggio);
    }
}
