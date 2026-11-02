using System.Diagnostics;
using System.Text.Json;
using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

/// <summary>6.5.0 punto 7: avanzamento guasto (servizio puro), retro-compatibilita JSON, esclusione dalla copertura
/// per Funzionalita, import guasto.</summary>
public sealed class DtcAvanzamentoServiceTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0);

    private static Requisito Req(string id, params (string Guasto, string Componente)[] voci) => new()
    {
        Id = id,
        Guasto = voci.Select(v => new DtcRequisito { Codice = v.Guasto, Componente = v.Componente }).ToList()
    };

    private static EsitoDtcRilevato E(string req, string guasto, string componente, PassoDtc passo, Esito esito, int giorno, int ord = 0) =>
        new(req, guasto, componente, passo, esito, T0.AddDays(giorno), "T-1", ord);

    [Fact]
    public void SenzaEsiti_TuttoDaFare()
    {
        var r = DtcAvanzamentoService.Calcola(new[] { Req("R1", ("P0001", "ECM"), ("P0002", "ECM")) }, Array.Empty<EsitoDtcRilevato>());
        Assert.Equal(2, r.Totale.Totale);
        Assert.Equal(0, r.Totale.Completi);
        Assert.All(r.PerDtc, d => Assert.Equal(StatoDtc.DaFare, d.Stato));
        Assert.Equal("0/2", r.Totale.Testo);
    }

    [Fact]
    public void Validato_Completo_NonConforme()
    {
        var reqs = new[] { Req("R1", ("A", "ECM"), ("B", "ECM"), ("C", "ECM")) };
        var esiti = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R1", "B", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R1", "B", "ECM", PassoDtc.Devalidazione, Esito.Conforme, 2),
            E("R1", "C", "ECM", PassoDtc.Validazione, Esito.NonConforme, 1),
        };
        var r = DtcAvanzamentoService.Calcola(reqs, esiti);
        Assert.Equal(StatoDtc.Validato, r.PerDtc.Single(d => d.Guasto == "A").Stato);
        Assert.Equal(StatoDtc.Completo, r.PerDtc.Single(d => d.Guasto == "B").Stato);
        Assert.Equal(StatoDtc.NonConforme, r.PerDtc.Single(d => d.Guasto == "C").Stato);
        Assert.Equal(1, r.Totale.Completi);
        Assert.False(r.PerRequisito.Single().Coperto);
    }

    [Fact]
    public void UltimoEsitoPerData_VinceAncheSeInseritoPrima()
    {
        var reqs = new[] { Req("R1", ("A", "ECM")) };
        // Non conforme il giorno 1, conforme il giorno 3 (inserito per primo): vince il piu recente.
        var esiti = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 3, 0),
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.NonConforme, 1, 1),
            E("R1", "A", "ECM", PassoDtc.Devalidazione, Esito.Conforme, 2, 2),
        };
        Assert.Equal(StatoDtc.Completo, DtcAvanzamentoService.Calcola(reqs, esiti).PerDtc.Single().Stato);

        // Il contrario: conforme prima, non conforme dopo -> torna non conforme.
        var esiti2 = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R1", "A", "ECM", PassoDtc.Devalidazione, Esito.Conforme, 1),
            E("R1", "A", "ECM", PassoDtc.Devalidazione, Esito.NonConforme, 4),
        };
        Assert.Equal(StatoDtc.NonConforme, DtcAvanzamentoService.Calcola(reqs, esiti2).PerDtc.Single().Stato);
    }

    [Fact]
    public void StessaData_ConflittoSegnalato_PrevaleNonConforme()
    {
        var reqs = new[] { Req("R1", ("A", "ECM")) };
        var esiti = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1, 0),
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.NonConforme, 1, 1),
        };
        var d = DtcAvanzamentoService.Calcola(reqs, esiti).PerDtc.Single();
        Assert.True(d.Conflitto);
        Assert.Equal(StatoDtc.NonConforme, d.Stato);
    }

    [Fact]
    public void StessaDataStessaClasse_NessunConflitto()
    {
        var reqs = new[] { Req("R1", ("A", "ECM")) };
        var esiti = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1, 0),
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1, 1),
        };
        Assert.False(DtcAvanzamentoService.Calcola(reqs, esiti).PerDtc.Single().Conflitto);
    }

    [Fact]
    public void ConformeConDeviazioni_ValeConforme_Completo()
    {
        var reqs = new[] { Req("R1", ("A", "ECM")) };
        var esiti = new[]
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R1", "A", "ECM", PassoDtc.Devalidazione, Esito.ConformeConDeviazioni, 2),
        };
        var d = DtcAvanzamentoService.Calcola(reqs, esiti).PerDtc.Single();
        Assert.Equal(StatoDtc.Completo, d.Stato);
        Assert.Equal(Esito.ConformeConDeviazioni, d.EsitoDevalidazione);
    }

    [Fact]
    public void RequisitoMultiDtc_CopertoSoloConTuttiCompleti()
    {
        var reqs = new[] { Req("R1", ("A", "ECM"), ("B", "ECM")), Req("R2", ("A", "ECM")) };
        var esiti = new List<EsitoDtcRilevato>
        {
            E("R1", "A", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R1", "A", "ECM", PassoDtc.Devalidazione, Esito.Conforme, 1),
        };
        var r = DtcAvanzamentoService.Calcola(reqs, esiti);
        Assert.False(r.PerRequisito.Single(x => x.RequisitoId == "R1").Coperto);
        // Il guasto "A" di R2 e una cella distinta: l'esito di R1 non la completa.
        Assert.False(r.PerRequisito.Single(x => x.RequisitoId == "R2").Coperto);
        esiti.Add(E("R1", "B", "ECM", PassoDtc.Validazione, Esito.Conforme, 2));
        esiti.Add(E("R1", "B", "ECM", PassoDtc.Devalidazione, Esito.Conforme, 2));
        r = DtcAvanzamentoService.Calcola(reqs, esiti);
        Assert.True(r.PerRequisito.Single(x => x.RequisitoId == "R1").Coperto);
        Assert.Equal(1, r.Totale.RequisitiCoperti);
        Assert.Equal(2, r.Totale.RequisitiTotali);
        Assert.Equal("2/3", r.Totale.Testo);
    }

    [Fact]
    public void EsitiSenzaVoce_NonAlteranoTotaliEVengonoContati_ArchiviatiEsclusi()
    {
        var arch = Req("R9", ("Z", "ECM")); arch.Archiviata = true;
        var reqs = new[] { Req("R1", ("A", "ECM")), arch };
        var esiti = new[]
        {
            E("R1", "TYPO", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
            E("R9", "Z", "ECM", PassoDtc.Validazione, Esito.Conforme, 1),
        };
        var r = DtcAvanzamentoService.Calcola(reqs, esiti);
        Assert.Equal(1, r.Totale.Totale);
        Assert.Equal(2, r.CelleSenzaAnagrafica);
    }

    [Fact]
    public void VociVuoteORequisitoSenzaDtc_NonCreanoCellePhantom()
    {
        var reqs = new[] { Req("R1", ("", "ECM"), ("A", ""), ("A", "ECM")), new Requisito { Id = "R2" } };
        var r = DtcAvanzamentoService.Calcola(reqs, Array.Empty<EsitoDtcRilevato>());
        Assert.Single(r.PerDtc);
    }

    // === Estrazione dai Test ==========================================================================

    private static TestCase TestConOp(string id, string componente, DateTime data, params Condizione[] cond) => new()
    {
        Id = id, Componente = new EcuSelection(new[] { componente }),
        Esecuzioni = { new Esecuzione { Iterazione = 1, Data = data, Condizioni = cond.ToList() } }
    };

    [Fact]
    public void RaccogliEsiti_SoloDtcEseguiti_UltimoTentativoPerOperazione()
    {
        var radice = Guid.NewGuid();
        var primo = new Condizione { Uid = radice, Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Validazione, Esito = Esito.NonConforme };
        var ripetizione = new Condizione { OperazioneOrigineUid = radice, Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme };
        var daEseguire = new Condizione { Guasto = "B", DtcRequisitoId = "R1", Esito = Esito.DaEseguire };
        var normale = new Condizione { Funzionalita = "F", Esito = Esito.Conforme };
        var tc = TestConOp("T-1", "ECM", T0, primo, ripetizione, daEseguire, normale);
        var esiti = DtcAvanzamentoService.RaccogliEsiti(new[] { tc });
        var e = Assert.Single(esiti);
        Assert.Equal(("R1", "A", "ECM", Esito.Conforme), (e.RequisitoId, e.Guasto, e.Componente, e.Esito));
    }

    [Fact]
    public void RaccogliEsiti_PassoDefaultValidazione_TestEliminatoIgnorato_EcuDellaOperazione()
    {
        var c = new Condizione { Guasto = "A", DtcRequisitoId = "R1", Esito = Esito.Conforme, Componente = new EcuSelection(new[] { "BCM", "ECM" }) };
        var tc = TestConOp("T-1", "ECM", T0, c);
        var eliminato = TestConOp("T-2", "ECM", T0, new Condizione { Guasto = "Q", DtcRequisitoId = "R1", Esito = Esito.Conforme });
        eliminato.Eliminato = true;
        var esiti = DtcAvanzamentoService.RaccogliEsiti(new[] { tc, eliminato });
        Assert.Equal(2, esiti.Count);
        Assert.All(esiti, x => Assert.Equal(PassoDtc.Validazione, x.Passo));
        Assert.Contains(esiti, x => x.Componente == "BCM");
    }

    [Fact]
    public void PiuTest_AvanzamentoSuInsiemeDtcDelRequisito()
    {
        var reqs = new[] { Req("R1", ("A", "ECM"), ("B", "ECM")) };
        var t1 = TestConOp("T-1", "ECM", T0, new Condizione { Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Validazione, Esito = Esito.Conforme });
        var t2 = TestConOp("T-2", "ECM", T0.AddDays(1), new Condizione { Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Devalidazione, Esito = Esito.Conforme });
        var r = DtcAvanzamentoService.Calcola(reqs, new[] { t1, t2 });
        Assert.Equal(StatoDtc.Completo, r.PerDtc.Single(d => d.Guasto == "A").Stato);
        Assert.Equal(StatoDtc.DaFare, r.PerDtc.Single(d => d.Guasto == "B").Stato);
    }

    // === Esclusione dalla copertura per Funzionalita ===================================================

    [Fact]
    public void OperazioniDtc_NonEntranoNellaCoperturaPerFunzionalita()
    {
        var f = new Funzionalita { Nome = "F1" };
        var tc = TestConOp("T-1", "ECM", T0, new Condizione { Guasto = "A", DtcRequisitoId = "R1", Esito = Esito.Conforme });
        tc.Funzionalita = new List<string> { "F1" };
        var bucket = CoverageService.PerFunzionalita(new[] { f }, new[] { tc });
        Assert.Equal(0, bucket.Conformi);

        // Controprova: la stessa operazione senza Guasto copre la funzionalita (nessuna regressione).
        var tc2 = TestConOp("T-2", "ECM", T0, new Condizione { Funzionalita = "F1", Esito = Esito.Conforme });
        tc2.Funzionalita = new List<string> { "F1" };
        Assert.Equal(1, CoverageService.PerFunzionalita(new[] { f }, new[] { tc2 }).Conformi);
    }

    // === Retro-compatibilita JSON ======================================================================

    [Fact]
    public void Requisito_JsonLegacySenzaDtc_SiLegge_E_RiscrittoIdentico()
    {
        const string legacy = "{\"Id\":\"R1\",\"Testo\":\"t\",\"ApplicabileBanco\":true,\"ApplicabileVeicolo\":false,\"Archiviata\":false}";
        var r = JsonSerializer.Deserialize<Requisito>(legacy, JsonIo.Options)!;
        Assert.Empty(r.DtcVoci);
        Assert.Null(r.Guasto);
        Assert.DoesNotContain("\"Guasto\"", JsonSerializer.Serialize(r, JsonIo.Options));
    }

    [Fact]
    public void Requisito_ConDtc_RoundTrip()
    {
        var r = Req("R1", ("P0001", "ECM"));
        var back = JsonSerializer.Deserialize<Requisito>(JsonSerializer.Serialize(r, JsonIo.Options), JsonIo.Options)!;
        Assert.Equal("P0001", Assert.Single(back.DtcVoci).Codice);
    }

    [Fact]
    public void Condizione_JsonLegacySenzaDtc_NessunCambioDiComportamento()
    {
        var c = new Condizione { Funzionalita = "F", Esito = Esito.Conforme };
        var json = JsonSerializer.Serialize(c, JsonIo.Options);
        Assert.DoesNotContain("Guasto", json);
        var back = JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!;
        Assert.False(back.RiguardaDtc);
        Assert.Null(back.DtcPasso);
    }

    [Fact]
    public void Condizione_ConDtc_RoundTrip_PassoComeStringa()
    {
        var c = new Condizione { Guasto = "A", DtcRequisitoId = "R1", DtcPasso = PassoDtc.Devalidazione, Esito = Esito.Conforme };
        var json = JsonSerializer.Serialize(c, JsonIo.Options);
        Assert.Contains("\"Devalidazione\"", json);
        var back = JsonSerializer.Deserialize<Condizione>(json, JsonIo.Options)!;
        Assert.Equal(("A", "R1", PassoDtc.Devalidazione), (back.Guasto, back.DtcRequisitoId, back.DtcPasso!.Value));
    }

    [Fact]
    public void SchemaVersion_Bump9()
    {
        Assert.Equal(11, TestCase.CurrentSchemaVersion);
    }

    // === Import ========================================================================================

    [Fact]
    public void Import_CsvConIntestazione_DelimitatoriEVirgolette()
    {
        var righe = DtcImportService.LeggiCsv("Guasto;componente\r\nP0001;ECM\r\n\"P0002\";\"BCM\"\r\nP0001;ECM\r\n");
        var r = DtcImportService.Interpreta(righe, Array.Empty<DtcRequisito>(), new[] { "ECM" });
        Assert.Equal(2, r.Nuove.Count);
        Assert.Equal(1, r.Duplicate);
        Assert.Empty(r.Errori);
        Assert.Equal(new[] { "BCM" }, r.EcuSconosciute);
    }

    [Fact]
    public void Import_SenzaIntestazione_Posizionale_E_VirgolaComeDelimitatore()
    {
        var r = DtcImportService.Interpreta(DtcImportService.LeggiCsv("P0001,ECM\nP0002,ECM"), Array.Empty<DtcRequisito>());
        Assert.Equal(2, r.Nuove.Count);
    }

    [Fact]
    public void Import_SaltaEsistenti_RigaInvalidaSegnalata()
    {
        var esistenti = new[] { new DtcRequisito { Codice = "p0001", Componente = "ecm" } };
        var r = DtcImportService.Interpreta(new List<List<string>> { new() { "P0001", "ECM" }, new() { "P0003", "" } }, esistenti);
        Assert.Equal(1, r.Duplicate);
        Assert.Single(r.Errori);
        Assert.Empty(r.Nuove);
    }

    // === Scala =========================================================================================

    [Fact]
    public void Scala_5000Dtc_100kTest_TempiContenuti()
    {
        var voci = Enumerable.Range(0, 5000).Select(i => ($"P{i:D4}", "ECM")).ToArray();
        var reqs = new[] { Req("R1", voci) };
        var tests = new List<TestCase>(100_000);
        for (var i = 0; i < 100_000; i++)
        {
            var tc = new TestCase { Id = "T" + i, Componente = new EcuSelection(new[] { "ECM" }) };
            var cond = new List<Condizione> { new() { Funzionalita = "F", Esito = Esito.Conforme } };
            if (i % 20 == 0) cond.Add(new Condizione { Guasto = voci[i / 20].Item1, DtcRequisitoId = "R1", DtcPasso = i % 40 == 0 ? PassoDtc.Validazione : PassoDtc.Devalidazione, Esito = Esito.Conforme });
            tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = T0, Condizioni = cond });
            tests.Add(tc);
        }
        var sw = Stopwatch.StartNew();
        var r = DtcAvanzamentoService.Calcola(reqs, tests);
        sw.Stop();
        Assert.Equal(5000, r.Totale.Totale);
        Assert.Equal(5000, r.Totale.Validati + r.Totale.DaFare);
        Assert.True(sw.ElapsedMilliseconds < 3000, $"Calcola 100k test/5000 guasto: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"[bench] Calcola 100k test / 5000 guasto: {sw.ElapsedMilliseconds} ms");
    }
}
