using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.5.1: i riferimenti per nome introdotti con i guasti per veicolo e le operazioni degli strumenti (Condizione.Strumento,
/// Condizione.DtcVeicolo, DtcRequisito.Veicolo anche negli archivi "requisiti-guasto") seguono rinomina/unione/divisione e contano come "in uso".</summary>
public sealed class MasterRiferimentiDtcStrumentoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "TestLoom_MasterRifDtc_" + Guid.NewGuid().ToString("N"));
    private readonly List<AppState> _stati = new();

    public void Dispose()
    {
        foreach (var s in _stati) { try { s.Dispose(); } catch { } }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AppState CreaStato()
    {
        var state = TestStateFactory.Create();
        Directory.CreateDirectory(_dir);
        state.InizializzaCartella(_dir);
        _stati.Add(state);
        var op = TestOperatori.Sigilla(new Operatore { Nome = "Op Prova", Ruolo = RuoloOperatore.Master, PinHash = PinService.Hash("1234") });
        state.Master.Operatori.Save(new List<Operatore> { op });
        Assert.True(state.ImpostaOperatore(op.Nome, "1234"));
        return state;
    }

    private static TestCase TestConOperazioni(string id, string veicolo, string strumento) => new()
    {
        Id = id, Veicolo = veicolo, Veicoli = { veicolo }, Componente = { "ECM" }, StrumentiUsati = { strumento },
        Esecuzioni = { new Esecuzione { Iterazione = 1, Condizioni = { new Condizione
        {
            Componente = { "ECM" }, Esito = Esito.DaEseguire, Strumento = strumento, Guasto = "P0001", DtcRequisitoId = "D1", DtcVeicolo = veicolo, DtcPasso = PassoDtc.Validazione,
        } } } },
    };

    [Fact]
    public void Registro_ContieneIRiferimentiPerNomeDelleOperazioniEDelleVociDtc()
    {
        bool Ha(Type t, string p, string cat) => MasterRiferimentiService.Registro.Any(r => r.TipoNodo == t && r.NomeProprieta == p && r.Catalogo == cat && r.Proprieta != null);
        Assert.True(Ha(typeof(Condizione), nameof(Condizione.Strumento), "StrumentoDiagnostico"));
        Assert.True(Ha(typeof(Condizione), nameof(Condizione.DtcVeicolo), "Veicolo"));
        Assert.True(Ha(typeof(DtcRequisito), nameof(DtcRequisito.Veicolo), "Veicolo"));
    }

    [Fact]
    public void InUso_UnaSolaOperazioneConStrumentoOVeicoloDtc_ContaComeTestCheLoReferenzia()
    {
        var tc = new TestCase { Id = "T-1", Esecuzioni = { new Esecuzione { Condizioni = { new Condizione { Strumento = "Guasto Injection", DtcVeicolo = "V9", Guasto = "P1" } } } } };
        Assert.True(MasterRiferimentiService.TestCaseReferenzia(tc, "StrumentoDiagnostico", "Guasto Injection"));
        Assert.True(MasterRiferimentiService.TestCaseReferenzia(tc, "Veicolo", "V9"));
        Assert.False(MasterRiferimentiService.TestCaseReferenzia(tc, "Veicolo", "V1"));
    }

    [Fact]
    public void RinominaStrumentoEVeicolo_AggiornanoLeOperazioniGiaGenerate()
    {
        var state = CreaStato();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" } });
        state.TestCases.Save(TestConOperazioni("T-1", "V1", "Guasto Injection"), isNew: true);

        MasterUnioneService.Rinomina(state, state.Master.Veicoli, "Veicolo", "V1", "V1b", v => v.Nome, new List<string>());
        var op = state.TestCases.GetAll().Single().Esecuzioni[0].Condizioni.Single();
        Assert.Equal("V1b", op.DtcVeicolo);
        Assert.Equal("Guasto Injection", op.Strumento);
    }

    [Fact]
    public void RinominaStrumento_AggiornaCondizioneStrumento()
    {
        var state = CreaStato();
        state.Master.Strumenti.Save(new List<StrumentoDiagnostico> { new() { Nome = "Guasto Injection" } });
        state.TestCases.Save(TestConOperazioni("T-1", "V1", "Guasto Injection"), isNew: true);

        MasterUnioneService.Rinomina(state, state.Master.Strumenti, "StrumentoDiagnostico", "Guasto Injection", "Guasto Inj", s => s.Nome, new List<string>());
        var t = state.TestCases.GetAll().Single();
        Assert.Equal("Guasto Inj", t.Esecuzioni[0].Condizioni.Single().Strumento);
        Assert.Equal("Guasto Inj", t.StrumentiUsati.Single());
    }

    private static Requisito Extra(string id, params (string Codice, string Componente, string? Veicolo)[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t" };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    [Fact]
    public void RinominaEUnisciVeicolo_AggiornanoLeVociDtcDegliArchiviEDeiRequisitiEmbedded_SenzaDuplicati()
    {
        var state = CreaStato();
        state.Master.Veicoli.Save(new List<Veicolo> { new() { Nome = "V1" }, new() { Nome = "V2" } });
        var arch = Extra("D1"); arch.DtcConteggio = 3;
        var emb = Extra("D2"); emb.Guasto = new List<DtcRequisito> { new() { Codice = "E1", Componente = "ECM", Veicolo = "V1" }, new() { Codice = "E2", Componente = "ECM" } };
        state.Master.Requisiti.Save(new List<Requisito> { arch, emb });
        var d1 = state.Master.Requisiti.Load().Single(r => r.Id == "D1");
        state.Master.RequisitiDtc.Scrivi(new[] { new DtcScrittura(d1, new List<DtcRequisito>
        {
            new() { Codice = "P1", Componente = "ECM", Veicolo = "V1" }, new() { Codice = "P1", Componente = "ECM", Veicolo = "V2" }, new() { Codice = "P2", Componente = "ECM", Veicolo = "V1" },
        }, null) }, "Seed");

        MasterUnioneService.Rinomina(state, state.Master.Veicoli, "Veicolo", "V1", "V1x", v => v.Nome, new List<string>());
        d1 = state.Master.Requisiti.Load().Single(r => r.Id == "D1");
        Assert.Equal(new[] { "V1x", "V2", "V1x" }, state.Master.RequisitiDtc.Leggi(d1).Voci.Select(v => v.Veicolo!).ToArray());
        var d2 = state.Master.Requisiti.Load().Single(r => r.Id == "D2");
        Assert.Equal("V1x", d2.Guasto!.Single(v => v.Codice == "E1").Veicolo);
        Assert.Null(d2.Guasto!.Single(v => v.Codice == "E2").Veicolo);               // voce senza veicolo: invariata

        var avvisi = new List<string>();
        MasterUnioneService.Unisci(state, state.Master.Veicoli, "Veicolo", "V1x", "V2", v => v.Nome, avvisi);
        d1 = state.Master.Requisiti.Load().Single(r => r.Id == "D1");
        var voci = state.Master.RequisitiDtc.Leggi(d1).Voci;
        Assert.All(voci, v => Assert.Equal("V2", v.Veicolo));
        Assert.Equal(new[] { "P1", "P2" }, voci.Select(v => v.Codice).OrderBy(c => c).ToArray());     // P1 collassa sulla chiave (V2, ECM, P1)
        Assert.Equal(2, d1.DtcConteggio);
        Assert.Equal("V2", state.Master.Requisiti.Load().Single(r => r.Id == "D2").Guasto!.Single(v => v.Codice == "E1").Veicolo);
    }
}
