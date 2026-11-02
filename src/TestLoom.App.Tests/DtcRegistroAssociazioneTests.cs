using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

[Collection("Localization")]
public sealed class DtcRegistroAssociazioneTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcRegistroAssociazione_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Associa_AggiungeUnaNuovaRevECollegaLaTuplaUsataDaAudit()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        var test = CreaTest("T-guasto-01", "V1", new[] { "ECM" });
        test.RequisitoId.Add("R-ALTRO");
        state.TestCases.Save(test, isNew: true);

        var riga = LeggiRiga(state);
        var servizio = new DtcRegistroAssociazioneService(state);
        Assert.Contains(servizio.RequisitiCompatibili(riga), r => r.Id == requisito.Id);

        var risultato = servizio.Associa(riga, requisito.Id);
        var corrente = state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!;
        var query = new LocalDtcRegistroQueryService(state.TestCases.LocalIndexDatabasePath!);
        var righeDopo = query.LeggiPagina(new(), 1, 20).Righe;
        var avanzamento = DtcAvanzamentoService.Calcola(new[] { requisito }, new[] { corrente });

        Assert.Equal(2, corrente.Esecuzioni.Count);
        Assert.Equal(2, risultato.NumeroNuovaRevisione);
        Assert.True(risultato.RevisioneDati > test.RevisioneDati);
        Assert.Null(corrente.Esecuzioni[0].Condizioni[0].DtcRequisitoId);
        Assert.Equal(requisito.Id, corrente.Esecuzioni[1].Condizioni[0].DtcRequisitoId);
        Assert.Contains("R-ALTRO", corrente.RequisitoId);
        Assert.Contains(requisito.Id, corrente.RequisitoId);
        Assert.Single(corrente.RequisitoId, id => id.Equals(requisito.Id, StringComparison.OrdinalIgnoreCase));
        var punteggio = Assert.Single(corrente.Esecuzioni[1].PunteggiRequisiti, p => p.RequisitoId == requisito.Id);
        Assert.Equal(PunteggioConformita.Conforme, punteggio.Valore);
        Assert.Contains(righeDopo, r => r.RevisioneIndice == 0 && r.Requisito.Length == 0);
        Assert.Contains(righeDopo, r => r.RevisioneIndice == 1 && r.Requisito == requisito.Id);
        Assert.Equal(StatoDtc.Validato, Assert.Single(avanzamento.PerDtc).Stato);
    }

    [Fact]
    public void Associa_RequisitoGiaCollegatoNonVieneDuplicato()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        const string requisitoConCasingDiverso = "r-extra";
        var test = CreaTest("T-guasto-GIA-COLLEGATO", "V1", new[] { "ECM" }, requisitoConCasingDiverso);
        state.TestCases.Save(test, isNew: true);

        new DtcRegistroAssociazioneService(state).Associa(LeggiRiga(state), requisito.Id);

        var corrente = state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!;
        Assert.Equal(requisitoConCasingDiverso, Assert.Single(corrente.RequisitoId, id => id.Equals(requisito.Id, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(requisito.Id, corrente.Esecuzioni[^1].Condizioni[0].DtcRequisitoId);
    }

    [Fact]
    public void RequisitiCompatibili_RichiedeLaTuplaCompletaPerTutteLeEcu()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-PARTIAL", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        var test = CreaTest("T-guasto-02", "V1", new[] { "ECM", "TCM" });
        state.TestCases.Save(test, isNew: true);
        var riga = LeggiRiga(state);

        var servizio = new DtcRegistroAssociazioneService(state);

        Assert.Empty(servizio.RequisitiCompatibili(riga));
        var errore = Assert.Throws<DtcRegistroAssociazioneException>(() => servizio.Associa(riga, requisito.Id));
        Assert.Equal(StatoAssociazioneDtcRegistro.RequisitoNonCompatibile, errore.Stato);
        var corrente = state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!;
        Assert.Single(corrente.Esecuzioni);
        Assert.Null(corrente.Esecuzioni[0].Condizioni[0].DtcRequisitoId);
    }

    [Fact]
    public void Associa_NonRidataLaProvaEAuditMantieneLEsitoPiuRecenteDiUnAltroTest()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        var precedente = CreaTest("T-PRECEDENTE", "V1", new[] { "ECM" });
        state.TestCases.Save(precedente, isNew: true);
        var piuRecente = CreaTest("T-PIU-RECENTE", "V1", new[] { "ECM" });
        piuRecente.Esecuzioni[0].Data = Day.AddHours(1);
        piuRecente.Esecuzioni[0].Operatore = "Altro operatore";
        piuRecente.Esecuzioni[0].Esito = Esito.NonConforme;
        piuRecente.Esecuzioni[0].Condizioni[0].Esito = Esito.NonConforme;
        piuRecente.Esecuzioni[0].Condizioni[0].DtcRequisitoId = requisito.Id;
        state.TestCases.Save(piuRecente, isNew: true);
        var rigaPrecedente = new LocalDtcRegistroQueryService(state.TestCases.LocalIndexDatabasePath!)
            .LeggiPagina(new(), 1, 20).Righe.Single(r => r.TestId == precedente.Id);

        new DtcRegistroAssociazioneService(state).Associa(rigaPrecedente, requisito.Id);

        var precedenteAggiornato = state.TestCases.LoadCurrent(state.TestCases.GetByUid(precedente.Uid)!)!;
        var testPiuRecenteAggiornato = state.TestCases.LoadCurrent(state.TestCases.GetByUid(piuRecente.Uid)!)!;
        var avanzamento = DtcAvanzamentoService.Calcola(new[] { requisito }, new[] { precedenteAggiornato, testPiuRecenteAggiornato });
        var cella = Assert.Single(avanzamento.PerDtc);

        Assert.Equal(Day, precedenteAggiornato.Esecuzioni[^1].Data);
        Assert.Equal("Master Prova", precedenteAggiornato.Esecuzioni[^1].Operatore);
        Assert.Equal(StatoDtc.NonConforme, cella.Stato);
        Assert.Equal("T-PIU-RECENTE", cella.TestIdValidazione);
        Assert.Equal(Day.AddHours(1), cella.DataValidazione);
    }

    [Fact]
    public async Task Associa_MessaggioRestaVisibileDopoRefreshConFiltroSenzaRequisito()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        state.TestCases.Save(CreaTest("T-guasto-04", "V1", new[] { "ECM" }), isNew: true);
        using var vm = new DtcRegistroViewModel(state);
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento, TimeSpan.FromSeconds(10)));
        vm.SoloSenzaRequisito = true;
        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento && vm.Righe.Count == 1, TimeSpan.FromSeconds(10)));
        vm.Selezionata = vm.Righe.Single();

        var handlerOriginale = SelezionaDialogService.Handler;
        try
        {
            SelezionaDialogService.Handler = (_, voci, _) => Task.FromResult<IReadOnlyList<VoceSelezionabile>>(new[] { voci[0] });
            await vm.AssociaRequisitoCommand.ExecuteAsync(null);
        }
        finally
        {
            SelezionaDialogService.Handler = handlerOriginale;
        }

        Assert.True(SpinWait.SpinUntil(() => !vm.Caricamento, TimeSpan.FromSeconds(10)));
        Assert.Null(vm.Selezionata);
        Assert.Single(vm.Righe);
        Assert.All(vm.Righe, r => Assert.Empty(r.Requisito));
        Assert.Contains("REV 2", vm.MessaggioPagina);
    }

    [Fact]
    public void Associa_RifiutaUnaRigaDiRevisioneStorica()
    {
        using var state = CreaStato();
        var requisito = RequisitoD4D("R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.Master.Requisiti.Save(new List<Requisito> { requisito });
        var test = CreaTest("T-guasto-03", "V1", new[] { "ECM" });
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 2, Data = Day.AddDays(1), Operatore = "Master Prova", Esito = Esito.Conforme,
            Condizioni = [CreaCondizione("V1", "ECM")]
        });
        state.TestCases.Save(test, isNew: true);
        var rigaStorica = new LocalDtcRegistroQueryService(state.TestCases.LocalIndexDatabasePath!)
            .LeggiPagina(new(), 1, 20).Righe.Single(r => r.RevisioneIndice == 0);

        var errore = Assert.Throws<DtcRegistroAssociazioneException>(() => new DtcRegistroAssociazioneService(state).Associa(rigaStorica, requisito.Id));
        var corrente = state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!;

        Assert.Equal(StatoAssociazioneDtcRegistro.RigaStorica, errore.Stato);
        Assert.Equal(2, corrente.Esecuzioni.Count);
        Assert.Null(corrente.Esecuzioni[0].Condizioni[0].DtcRequisitoId);
        Assert.Null(corrente.Esecuzioni[1].Condizioni[0].DtcRequisitoId);
    }

    private AppState CreaStato()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore
        {
            Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234")
        });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        return state;
    }

    private static Requisito RequisitoD4D(string id, params DtcRequisito[] guasto)
    {
        var requisito = new Requisito { Id = id, Testo = "Requisito Extra di prova", Guasto = guasto.ToList() };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        return requisito;
    }

    private static TestCase CreaTest(string id, string veicolo, string[] componenti, string? requisito = null)
    {
        var test = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day,
            ModificatoDa = "Master Prova", Veicolo = veicolo, Veicoli = [veicolo], Componente = new EcuSelection(componenti)
        };
        if (!string.IsNullOrWhiteSpace(requisito)) test.RequisitoId.Add(requisito);
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = Day, Operatore = "Master Prova", Esito = Esito.Conforme,
            Condizioni = [CreaCondizione(veicolo, componenti)]
        });
        return test;
    }

    private static Condizione CreaCondizione(string veicolo, params string[] componenti) => new()
    {
        Guasto = "P0100", DtcPasso = PassoDtc.Validazione, DtcVeicolo = veicolo,
        Esito = Esito.Conforme, Componente = new EcuSelection(componenti)
    };

    private static RigaDtcRegistro LeggiRiga(AppState state) => new LocalDtcRegistroQueryService(state.TestCases.LocalIndexDatabasePath!)
        .LeggiPagina(new(), 1, 20).Righe.First(r => r.Componente.Equals("ECM", StringComparison.OrdinalIgnoreCase));
}
