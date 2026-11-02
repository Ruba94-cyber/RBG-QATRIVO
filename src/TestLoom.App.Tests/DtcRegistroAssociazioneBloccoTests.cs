using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>Associazione guasto → requisito Extra dalla lista: candidati ordinati, aggiunta dei guasti mancanti al requisito (anche senza guasto),
/// associazione in blocco con una sola REV per Test e senza toccare le REV storiche.</summary>
[Collection("Localization")]
public sealed class DtcRegistroAssociazioneBloccoTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 5, 4, 9, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcAssocBlocco_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private AppState CreaStato()
    {
        Directory.CreateDirectory(_root);
        var state = TestStateFactory.Create();
        state.InizializzaCartella(_root);
        var master = TestOperatori.Sigilla(new Operatore { Nome = "Master Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { master });
        Assert.True(state.ImpostaOperatore(master.Nome, "1234"));
        // Anagrafica: le voci guasto di un requisito devono usare veicoli ed componente esistenti (stesse regole dell'editor).
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        state.Master.Componente.Save(new List<Componente> { new() { Nome = "ECM" } });
        return state;
    }

    /// <summary>Requisito Extra nel formato Phase D: il requisito porta solo il conteggio, i guasti stanno nell'archivio separato.</summary>
    private static void SeminaRequisito(AppState state, string id, params DtcRequisito[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "Requisito Extra di prova", DtcConteggio = guasto.Length };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        var esistenti = state.Master.Requisiti.Load().ToList();
        esistenti.Add(r);
        state.Master.Requisiti.Save(esistenti);
        if (guasto.Length > 0)
            state.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(state.Master.Requisiti.Load().Single(x => x.Id == id), guasto, null) }, "Seed");
    }

    private static Condizione Condizione(string guasto, string veicolo, string componente, PassoDtc passo = PassoDtc.Validazione) => new()
    {
        Guasto = guasto, DtcPasso = passo, DtcVeicolo = veicolo, Esito = Esito.Conforme, Componente = new EcuSelection([componente])
    };

    private static TestCase CreaTest(string id, string veicolo, string componente, params string[] guasto)
    {
        var test = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, ModificatoDa = "Master Prova",
            Veicolo = veicolo, Veicoli = [veicolo], Componente = new EcuSelection([componente])
        };
        test.Esecuzioni.Add(new Esecuzione
        {
            Iterazione = 1, Data = Day, Operatore = "Master Prova", Esito = Esito.Conforme,
            Condizioni = guasto.Select(d => Condizione(d, veicolo, componente)).ToList()
        });
        return test;
    }

    private static List<RigaDtcRegistro> Righe(AppState state) =>
        new LocalDtcRegistroQueryService(state.TestCases.LocalIndexDatabasePath!).LeggiPagina(new(), 1, 50).Righe.ToList();

    [Fact]
    public void Candidati_ProponeInOrdineCompatibiliSenzaDtcEAltri()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-ALTRO", new DtcRequisito { Codice = "P9999", Componente = "ECM", Veicolo = "V1" });
        SeminaRequisito(state, "R-VUOTO");
        SeminaRequisito(state, "R-COMPAT", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        state.TestCases.Save(CreaTest("T-1", "V1", "ECM", "P0100"), isNew: true);

        var candidati = new DtcRegistroAssociazioneService(state).Candidati(Righe(state).Single());

        Assert.Equal(new[] { "R-COMPAT", "R-VUOTO", "R-ALTRO" }, candidati.Select(c => c.Requisito.Id));
        Assert.Equal(new[] { true, false, false }, candidati.Select(c => c.Compatibile));
        Assert.Equal(new[] { 1, 0, 1 }, candidati.Select(c => c.NumeroDtc));
    }

    [Fact]
    public void AssociaInBlocco_AggiungeIDtcAlRequisitoSenzaDtcEFaUnaSolaRevPerTest()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-VUOTO");
        var test = CreaTest("T-1", "V1", "ECM", "P0100", "P0200");
        state.TestCases.Save(test, isNew: true);
        var servizio = new DtcRegistroAssociazioneService(state);
        var righe = Righe(state);
        Assert.Equal(2, righe.Count);

        var esito = servizio.AssociaInBlocco(righe, "R-VUOTO", aggiungiDtc: true);

        Assert.Equal(2, esito.Associate);
        Assert.Equal(0, esito.NonAssociate);
        Assert.Equal(2, esito.DtcAggiuntiAlRequisito);
        var requisito = state.Master.Requisiti.Load().Single(r => r.Id == "R-VUOTO");
        Assert.Equal(new[] { "P0100", "P0200" }, state.Master.RequisitiDtc.Leggi(requisito).Voci.Select(v => v.Codice).OrderBy(c => c));
        var corrente = state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!;
        Assert.Equal(2, corrente.Esecuzioni.Count); // una sola nuova REV per due righe dello stesso Test
        Assert.All(corrente.Esecuzioni[0].Condizioni, c => Assert.Null(c.DtcRequisitoId)); // REV storica intatta
        Assert.All(corrente.Esecuzioni[1].Condizioni, c => Assert.Equal("R-VUOTO", c.DtcRequisitoId));
        Assert.Single(corrente.RequisitoId, id => id == "R-VUOTO");
        Assert.All(Righe(state).Where(r => r.RevisioneIndice == 1), r => Assert.Equal("R-VUOTO", r.Requisito));
    }

    [Fact]
    public void AssociaInBlocco_SenzaAggiungereDtc_LasciaNonAssociateLeRigheNonCompatibili()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-VUOTO");
        var test = CreaTest("T-1", "V1", "ECM", "P0100");
        state.TestCases.Save(test, isNew: true);

        var esito = new DtcRegistroAssociazioneService(state).AssociaInBlocco(Righe(state), "R-VUOTO", aggiungiDtc: false);

        Assert.Equal(0, esito.Associate);
        Assert.Equal(1, esito.NonAssociate);
        Assert.Contains("compatibile", esito.Righe.Single().Errore);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!.Esecuzioni);
        Assert.Empty(state.Master.RequisitiDtc.Leggi(state.Master.Requisiti.Load().Single(r => r.Id == "R-VUOTO")).Voci);
    }

    [Fact]
    public void AggiungiDtcAlRequisito_E_Idempotente()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-Extra", new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" });
        var requisito = state.Master.Requisiti.Load().Single();
        var servizio = new DtcRegistroAssociazioneService(state);
        var voci = new[]
        {
            new DtcRequisito { Codice = "P0100", Componente = "ECM", Veicolo = "V1" }, // già presente
            new DtcRequisito { Codice = "p0100", Componente = "ecm", Veicolo = "v1" }, // stessa chiave, altre maiuscole
            new DtcRequisito { Codice = "P0200", Componente = "ECM", Veicolo = "V1" },
        };

        Assert.Equal(1, servizio.AggiungiDtcAlRequisito(requisito, voci).Aggiunte);
        Assert.Equal(0, servizio.AggiungiDtcAlRequisito(requisito, voci).Aggiunte);
        Assert.Equal(2, state.Master.RequisitiDtc.Leggi(requisito).Voci.Count);
    }

    [Fact]
    public void AssociaInBlocco_UnaRigaStoricaNonAssociataELAltraSi()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-VUOTO");
        var test = CreaTest("T-1", "V1", "ECM", "P0100");
        state.TestCases.Save(test, isNew: true);
        var riga = Righe(state).Single();
        var servizio = new DtcRegistroAssociazioneService(state);
        servizio.AssociaInBlocco([riga], "R-VUOTO", aggiungiDtc: true);

        // La stessa riga (ora REV storica) non è più associabile; un secondo Test viene associato normalmente.
        state.TestCases.Save(CreaTest("T-2", "V1", "ECM", "P0300"), isNew: true);
        var nuova = Righe(state).Single(r => r.TestId == "T-2");
        var esito = servizio.AssociaInBlocco([riga, nuova], "R-VUOTO", aggiungiDtc: true);

        Assert.Equal(1, esito.Associate);
        Assert.Equal(1, esito.NonAssociate);
        Assert.NotNull(esito.Righe.Single(r => r.Riga.TestId == "T-2").Esito);
        Assert.NotNull(esito.Righe.Single(r => r.Riga.TestId == "T-1").Errore);
    }

    [Fact]
    public void ServizioInSolaLettura_NonModificaNulla()
    {
        using var state = CreaStato();
        SeminaRequisito(state, "R-VUOTO");
        state.TestCases.Save(CreaTest("T-1", "V1", "ECM", "P0100"), isNew: true);
        var riga = Righe(state).Single();
        // Una seconda postazione sulla stessa cartella, senza operatore abilitato.
        using var lettura = TestStateFactory.Create();
        lettura.InizializzaCartella(_root);
        Assert.False(lettura.PuoScrivere);

        Assert.Throws<UnauthorizedAccessException>(() => new DtcRegistroAssociazioneService(lettura).AssociaInBlocco([riga], "R-VUOTO", true));
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetByUid(Guid.Parse(riga.TestUid))!)!.Esecuzioni);
    }

    [Fact]
    public void Associa_RispettaLeRegoleDelRequisito_VeicoloNonDichiaratoERifiutatoConIlMotivo()
    {
        using var state = CreaStato();
        // Il requisito dichiara solo V2: un guasto del veicolo V1 non può entrarvi.
        var r = new Requisito { Id = "R-V2", Testo = "t", DtcConteggio = 0 };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(r, new[] { "V2" });
        state.Master.Requisiti.Save(new List<Requisito> { r });
        var test = CreaTest("T-1", "V1", "ECM", "P0100");
        state.TestCases.Save(test, isNew: true);

        var esito = new DtcRegistroAssociazioneService(state).AssociaInBlocco(Righe(state), "R-V2", aggiungiDtc: true);

        Assert.Equal(0, esito.Associate);
        Assert.Equal(0, esito.DtcAggiuntiAlRequisito);
        Assert.Contains(LocalizationService.Instance.Traduci("DtcVeicoloNonDichiarato"), esito.Righe.Single().Errore);
        Assert.Empty(state.Master.RequisitiDtc.Leggi(state.Master.Requisiti.Load().Single()).Voci);
        Assert.Single(state.TestCases.LoadCurrent(state.TestCases.GetByUid(test.Uid)!)!.Esecuzioni);
    }
}
