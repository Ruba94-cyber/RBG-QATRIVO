using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.5.1 fase 1 (schema dati 9): Veicolo del guasto, Tipo dello strumento, Strumento/DtcVeicolo dell'operazione e
/// regole pure di DtcOperazioneRegole. Roundtrip JSON additivo: i file 6.5.0 senza i campi nuovi si leggono, i null non si scrivono,
/// i campi sconosciuti (ExtensionData) si preservano.</summary>
public sealed class DtcVeicoloModelloTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o, JsonIo.Options);
    private static T Leggi<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonIo.Options)!;

    // === DtcRequisito ====================================================================================

    [Fact]
    public void DtcRequisito_SenzaVeicolo_ByteIdenticoAlFormato650()
    {
        var json = Json(new DtcRequisito { Codice = "P0001", Componente = "ECM" });
        Assert.DoesNotContain("Veicolo", json);
        Assert.DoesNotContain("ExtensionData", json);
    }

    [Fact]
    public void DtcRequisito_File650SenzaCampoNuovo_SiLegge()
    {
        var v = Leggi<DtcRequisito>("{\"Codice\":\"P0001\",\"Componente\":\"ECM\"}");
        Assert.Null(v.Veicolo);
        Assert.Equal(DtcRequisito.SenzaVeicolo, v.VeicoloVista);
    }

    [Fact]
    public void DtcRequisito_ConSottoveicolo_RoundTrip_ECampiSconosciutiPreservati()
    {
        var v = Leggi<DtcRequisito>("{\"Codice\":\"P0001\",\"Componente\":\"ECM\",\"Veicolo\":\"Daily MY24\",\"Futuro\":{\"a\":1}}");
        Assert.Equal("Daily MY24", v.Veicolo);
        var riscritto = Json(v);
        Assert.Contains("\"Veicolo\"", riscritto);
        Assert.Contains("\"Futuro\"", riscritto);
        Assert.Contains("\"Futuro\"", Json(v.Clona()));
        Assert.Equal("Daily MY24", v.Clona().Veicolo);
    }

    [Fact]
    public void DtcRequisito_Chiave_DistingueISottoveicoli()
    {
        var a = new DtcRequisito { Codice = "P0001", Componente = "ECM", Veicolo = "A" };
        var b = new DtcRequisito { Codice = "P0001", Componente = "ECM", Veicolo = "B" };
        var senza = new DtcRequisito { Codice = "P0001", Componente = "ECM" };
        Assert.NotEqual(DtcRequisito.Chiave(a), DtcRequisito.Chiave(b));
        Assert.NotEqual(DtcRequisito.Chiave(a), DtcRequisito.Chiave(senza));
        Assert.Equal(DtcRequisito.Chiave(senza), DtcRequisito.Chiave(new DtcRequisito { Codice = " P0001 ", Componente = "ECM ", Veicolo = "  " }));
    }

    [Fact]
    public void Etichetta_IncludeIlSottoveicolo_SoloSePresente()
    {
        Assert.Equal("P0001 · ECM · R1", DtcRequisito.Etichetta("R1", "P0001", "ECM"));
        Assert.Equal("P0001 · ECM · Daily MY24 · R1", DtcRequisito.Etichetta("R1", "P0001", "ECM", "Daily MY24"));
        Assert.Equal("P0001 · ECM · Daily MY24 · R1", new DtcOpzione("R1", "P0001", "ECM", "Daily MY24").Etichetta);
        Assert.Equal("P0001 · ECM · R1", new DtcOpzione("R1", "P0001", "ECM").ToString());
        var c = new Condizione { Guasto = "P0001", DtcRequisitoId = "R1", DtcVeicolo = "Daily MY24", Componente = new EcuSelection(new[] { "ECM" }) };
        Assert.Equal("P0001 · ECM · Daily MY24 · R1", c.DtcEtichetta);
    }

    // === StrumentoDiagnostico.Tipo =======================================================================

    [Theory]
    [InlineData("Easy", TipoStrumento.DaTestare)]
    [InlineData("UnitLogger", TipoStrumento.DaTestare)]
    [InlineData("unitlogger", TipoStrumento.DaTestare)]
    [InlineData("Guasto Injection", TipoStrumento.Comportamento)]
    [InlineData("guasto injection", TipoStrumento.Comportamento)]
    [InlineData("ValidationTool", TipoStrumento.Supporto)]
    [InlineData("CaptureTool", TipoStrumento.Supporto)]
    [InlineData("capturetool", TipoStrumento.Supporto)]
    [InlineData("Breakout Box", TipoStrumento.Supporto)]
    [InlineData("", TipoStrumento.Supporto)]
    public void Strumento_TipoEffettivo_ClassificazioneNotaPerNome(string nome, TipoStrumento atteso) =>
        Assert.Equal(atteso, new StrumentoDiagnostico { Nome = nome }.TipoEffettivo);

    [Fact]
    public void Strumento_TipoEsplicito_VinceSullaClassificazionePerNome()
    {
        Assert.Equal(TipoStrumento.Supporto, new StrumentoDiagnostico { Nome = "UnitLogger", Tipo = TipoStrumento.Supporto }.TipoEffettivo);
        Assert.Equal(TipoStrumento.DaTestare, new StrumentoDiagnostico { Nome = "Altro", Tipo = TipoStrumento.DaTestare }.TipoEffettivo);
    }

    [Fact]
    public void Strumento_Tipo_NullNonScritto_ELegacySiLegge_ConStringa()
    {
        Assert.DoesNotContain("\"Tipo\"", Json(new StrumentoDiagnostico { Nome = "ValidationTool" }));
        var legacy = Leggi<StrumentoDiagnostico>("{\"Nome\":\"UnitLogger\",\"Campi\":[]}");
        Assert.Null(legacy.Tipo);
        Assert.Equal(TipoStrumento.DaTestare, legacy.TipoEffettivo);
        var json = Json(new StrumentoDiagnostico { Nome = "X", Tipo = TipoStrumento.Comportamento });
        Assert.Contains("\"Comportamento\"", json);
        Assert.Equal(TipoStrumento.Comportamento, Leggi<StrumentoDiagnostico>(json).Tipo);
    }

    // === Condizione.Strumento / DtcVeicolo ==========================================================

    [Fact]
    public void Condizione_CampiNuoviNull_NonScritti_ELegacySiLegge()
    {
        var json = Json(new Condizione { Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Validazione });
        Assert.DoesNotContain("Strumento", json);
        Assert.DoesNotContain("DtcVeicolo", json);
        Assert.DoesNotContain("ExtensionData", json);
        var legacy = Leggi<Condizione>("{\"Guasto\":\"A\",\"DtcRequisitoId\":\"R1\",\"DtcPasso\":\"Validazione\",\"Esito\":\"Conforme\"}");
        Assert.Null(legacy.Strumento);
        Assert.Null(legacy.DtcVeicolo);
        Assert.Equal(Esito.Conforme, legacy.Esito);
    }

    [Fact]
    public void Condizione_RoundTrip_ConCampiNuoviECampiSconosciuti()
    {
        var c = Leggi<Condizione>("{\"Guasto\":\"A\",\"Strumento\":\"UnitLogger\",\"DtcVeicolo\":\"Daily MY24\",\"DallaVersioneFutura\":42}");
        Assert.Equal("UnitLogger", c.Strumento);
        Assert.Equal("Daily MY24", c.DtcVeicolo);
        var riscritto = Json(c);
        Assert.Contains("\"DallaVersioneFutura\"", riscritto);
        var back = Leggi<Condizione>(riscritto);
        Assert.Equal(("UnitLogger", "Daily MY24"), (back.Strumento, back.DtcVeicolo));
    }

    [Fact]
    public void RevisioneCondizione_CambiaStrumentoODtcVeicolo_CreaUnaNuovaRev()
    {
        TestCase Base() => new()
        {
            Id = "T",
            Esecuzioni = { new Esecuzione { Iterazione = 1, Operatore = "Op", Condizioni = { new Condizione { Guasto = "A", DtcRequisitoId = "R1", Esito = Esito.Conforme } } } }
        };
        foreach (var modifica in new Action<Condizione>[] { c => c.Strumento = "UnitLogger", c => c.DtcVeicolo = "Daily MY24" })
        {
            var persisted = Base();
            var candidate = Leggi<TestCase>(Json(persisted));
            modifica(candidate.Esecuzioni[0].Condizioni[0]);
            Assert.True(TestRevisionSafety.PreserveImmutableHistory(persisted, candidate, DateTime.Now, "Tester"));
            Assert.Equal(2, candidate.Esecuzioni.Count);
        }
    }

    [Fact]
    public void TestCase_Schema9()
    {
        Assert.Equal(11, TestCase.CurrentSchemaVersion);
        Assert.Equal(11, new TestCase().SchemaVersion);
    }

    // === DtcOperazioneRegole: abbinamento per veicolo ===============================================

    private static Requisito Extra(string id, params DtcRequisito[] guasto)
    {
        var r = new Requisito { Id = id, Testo = "t", Guasto = guasto.ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    [Fact]
    public void Abbina_FiltraPerSottoveicolo_VuotoEQualunque()
    {
        var r1 = Extra("R1", new DtcRequisito { Codice = "P0001", Componente = "ECM", Veicolo = "A" });
        var r2 = Extra("R2", new DtcRequisito { Codice = "P0001", Componente = "ECM", Veicolo = "B" });
        var componente = new[] { "ECM" };
        Assert.Equal("R1", DtcOperazioneRegole.Abbina(new[] { r1, r2 }, "P0001", componente, null, "A"));
        Assert.Equal("R2", DtcOperazioneRegole.Abbina(new[] { r1, r2 }, "P0001", componente, null, " b "));
        Assert.Null(DtcOperazioneRegole.Abbina(new[] { r1, r2 }, "P0001", componente, null, "C"));
        // vuoto/null = qualunque: invariato (con componente specificata vince l'Id minore)
        Assert.Equal("R1", DtcOperazioneRegole.Abbina(new[] { r1, r2 }, "P0001", componente));
        Assert.Equal("R1", DtcOperazioneRegole.Abbina(new[] { r1, r2 }, "P0001", componente, null, ""));
        // con componente vuota e due candidati l'abbinamento e ambiguo; il veicolo lo risolve
        Assert.True(DtcOperazioneRegole.AbbinaConEsito(new[] { r1, r2 }, "P0001", Array.Empty<string>()).Ambiguo);
        Assert.Equal(("R2", false), DtcOperazioneRegole.AbbinaConEsito(new[] { r1, r2 }, "P0001", Array.Empty<string>(), null, "B"));
    }

    [Fact]
    public void Abbina_VoceSenzaVeicolo_NonCorrispondeASottoveicoloSpecifico_MaVaBeneConVuoto()
    {
        var r = Extra("R1", new DtcRequisito { Codice = "P0001", Componente = "ECM" });
        Assert.Equal("R1", DtcOperazioneRegole.Abbina(new[] { r }, "P0001", new[] { "ECM" }));
        Assert.Null(DtcOperazioneRegole.Abbina(new[] { r }, "P0001", new[] { "ECM" }, null, "A"));
    }

    // === DtcOperazioneRegole: operazioni attese / coppia / deselezione ====================================

    private static DtcRequisito V(string cod, string componente, string? sv = null) => new() { Codice = cod, Componente = componente, Veicolo = sv };

    [Fact]
    public void CoppiaDtc_ValidazioneEDevalidazione() =>
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, DtcOperazioneRegole.CoppiaDtc());

    [Theory]
    [InlineData(TipoStrumento.Supporto)]
    [InlineData(TipoStrumento.DaTestare)]   // UnitLogger/Easy lavorano per funzionalita, non per coppia guasto
    public void OperazioniAttese_SoloIlComportamentoHaLaCoppiaDtc(TipoStrumento tipo) =>
        Assert.Empty(DtcOperazioneRegole.OperazioniAttese(tipo, "X", "R1", new[] { V("P1", "ECM") }));

    [Theory]
    [InlineData(TipoStrumento.Comportamento)]
    public void OperazioniAttese_UnaCoppiaPerOgniDtc_OrdineDeterministico_SenzaDuplicati(TipoStrumento tipo)
    {
        var guasto = new[] { V("P2", "ECM", "B"), V("P1", "ECM", "B"), V("P1", "BCM"), V("P1", "ECM", "B"), V("P1", "ECM", "A") };
        var op = DtcOperazioneRegole.OperazioniAttese(tipo, "UnitLogger", "R1", guasto);
        Assert.Equal(8, op.Count);   // 4 guasto distinti x 2 passi
        Assert.All(op, o => Assert.Equal(("UnitLogger", "R1"), (o.Strumento, o.RequisitoId)));
        Assert.Equal(new (string?, string, string)[]
            { (null, "BCM", "P1"), (null, "BCM", "P1"), ("A", "ECM", "P1"), ("A", "ECM", "P1"), ("B", "ECM", "P1"), ("B", "ECM", "P1"), ("B", "ECM", "P2"), ("B", "ECM", "P2") },
            op.Select(o => (o.Veicolo, o.Componente, o.Codice)).ToArray());
        Assert.Equal(new[] { PassoDtc.Validazione, PassoDtc.Devalidazione }, op.Take(2).Select(o => o.Passo));
        Assert.Equal(op, DtcOperazioneRegole.OperazioniAttese(tipo, "UnitLogger", "R1", guasto.Reverse()));
    }

    [Fact]
    public void PuoDeselezionare_FalseSeEsitoORevRegistrati_TrueSoloSeNienteRegistrato()
    {
        static Condizione Op(Esito e) => new() { Strumento = "UnitLogger", Esito = e };
        Assert.True(DtcOperazioneRegole.PuoDeselezionare(Array.Empty<Condizione>()));
        Assert.True(DtcOperazioneRegole.PuoDeselezionare(new[] { Op(Esito.DaEseguire), Op(Esito.DaEseguire) }));
        Assert.False(DtcOperazioneRegole.PuoDeselezionare(new[] { Op(Esito.DaEseguire), Op(Esito.Conforme) }));
        Assert.False(DtcOperazioneRegole.PuoDeselezionare(new[] { Op(Esito.NonConforme) }));
        Assert.False(DtcOperazioneRegole.PuoDeselezionare(new[] { Op(Esito.NonApplicabile) }));
        Assert.False(DtcOperazioneRegole.PuoDeselezionare(new[] { Op(Esito.DaEseguire) }, haRevRegistrate: true));
    }
}
