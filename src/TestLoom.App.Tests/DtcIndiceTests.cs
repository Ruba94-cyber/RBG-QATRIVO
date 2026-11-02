using System.Diagnostics;
using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace TestLoom.App.Tests;

/// <summary>6.5.0 punto 7: proiezione SQLite test_dtc_operation (indice test v10) e lettura in streaming. Parita con il
/// calcolo in memoria, migrazione ricostruibile dall'indice v8, idempotenza/recovery, annullamento, benchmark 100k guasto.</summary>
public sealed class DtcIndiceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_DtcIdx_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Day = new(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc);

    public DtcIndiceTests(ITestOutputHelper o) { _out = o; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Condizione Op(string req, string guasto, PassoDtc passo, Esito esito, string? componente = null) => new()
    {
        Guasto = guasto, DtcRequisitoId = req, DtcPasso = passo, Esito = esito,
        Componente = componente == null ? new EcuSelection() : new EcuSelection(new[] { componente })
    };

    private static TestCase Test(string id, int ora, params Condizione[] ops)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, ModificatoDa = "x",
            Componente = new EcuSelection(new[] { "ECM" }), Veicoli = ["V"], Veicolo = "V"
        };
        tc.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = Day.AddHours(ora), Operatore = "op", Esito = Esito.Conforme, Condizioni = ops.ToList() });
        return tc;
    }

    private static Requisito Req(string id, params string[] guasto)
    {
        var r = new Requisito { Id = id, Guasto = guasto.Select(d => new DtcRequisito { Codice = d, Componente = "ECM" }).ToList() };
        RequisitoApplicabilita.ImpostaTipo(r, TipoRequisito.Extra);
        return r;
    }

    private LocalTestIndexStore NewStore(IEnumerable<TestCase> tests)
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in tests) store.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);
        return store;
    }

    private static List<TestCase> Dataset() => new()
    {
        Test("T1", 1, Op("R1", "A", PassoDtc.Validazione, Esito.Conforme), Op("R1", "B", PassoDtc.Validazione, Esito.NonConforme, "ECM")),
        Test("T2", 5, Op("R1", "A", PassoDtc.Devalidazione, Esito.ConformeConDeviazioni), Op("R1", "B", PassoDtc.Validazione, Esito.Conforme, "ECM"),
            new Condizione { Funzionalita = "F", Esito = Esito.Conforme }),
        Test("T3", 6, Op("R1", "C", PassoDtc.Validazione, Esito.DaEseguire)), // non eseguito: escluso
        Test("T4", 7, Op("R2", "A", PassoDtc.Validazione, Esito.Conforme)),
    };

    private static string Fp(DtcAvanzamentoRisultato r) =>
        string.Join("|", r.PerDtc.Select(d => $"{d.Chiave}:{d.Stato}")) + $"#{r.Totale.Testo}";

    [Fact]
    public void Streaming_ParitaConCalcoloInMemoria_TestEliminatoEsclusoEOperazioniNonEseguite()
    {
        var tests = Dataset();
        var eliminato = Test("T5", 9, Op("R1", "C", PassoDtc.Validazione, Esito.Conforme)); eliminato.Eliminato = true;
        tests.Add(eliminato);
        var store = NewStore(tests);
        var reqs = new[] { Req("R1", "A", "B", "C"), Req("R2", "A") };

        var inMemoria = DtcAvanzamentoService.Calcola(reqs, tests);
        var daIndice = DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(store.DatabasePath).StreamEsiti());
        Assert.Equal(Fp(inMemoria), Fp(daIndice));
        Assert.Equal(StatoDtc.Completo, daIndice.PerDtc.Single(d => d.RequisitoId == "R1" && d.Guasto == "A").Stato);
        Assert.Equal(StatoDtc.DaFare, daIndice.PerDtc.Single(d => d.Guasto == "C").Stato);
    }

    [Fact]
    public void Migrazione_DaIndiceV8_RicostruisceLaProiezioneDalCacheJson_Idempotente()
    {
        var tests = Dataset();
        var store = NewStore(tests);
        var reqs = new[] { Req("R1", "A", "B", "C"), Req("R2", "A") };
        var atteso = Fp(DtcAvanzamentoService.Calcola(reqs, tests));
        var db = store.DatabasePath;
        SqliteConnection.ClearAllPools();

        // Indice v8 reale: nessuna tabella guasto.
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE test_dtc_operation; UPDATE meta SET value = '8' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var q = new LocalDtcQueryService(riaperto.DatabasePath);
        Assert.True(q.Disponibile());
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(reqs, q.StreamEsiti())));

        // Idempotenza: forzare di nuovo la migrazione non duplica ne perde righe.
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE meta SET value = '8' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var terzo = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(terzo.DatabasePath).StreamEsiti())));
    }

    [Fact]
    public void Recovery_ProiezioneSvuotataAMano_SiRicostruisceForzandoLaMigrazione()
    {
        var tests = Dataset();
        var store = NewStore(tests);
        var reqs = new[] { Req("R1", "A", "B", "C"), Req("R2", "A") };
        var atteso = Fp(DtcAvanzamentoService.Calcola(reqs, tests));
        var db = store.DatabasePath;
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM test_dtc_operation; UPDATE meta SET value = '8' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(riaperto.DatabasePath).StreamEsiti())));
    }

    [Fact]
    public void ReUpsert_AggiornaLeOperazioniDtc_SenzaResiduiDelloStatoPrecedente()
    {
        var tc = Test("T1", 1, Op("R1", "A", PassoDtc.Validazione, Esito.NonConforme));
        var store = NewStore(new[] { tc });
        var reqs = new[] { Req("R1", "A") };
        Assert.Equal(StatoDtc.NonConforme, DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(store.DatabasePath).StreamEsiti()).PerDtc.Single().Stato);

        tc.Esecuzioni[0].Condizioni[0].Esito = Esito.Conforme;
        store.Upsert(Path.Combine(_root, "T1.json"), Day.AddMinutes(1), 101, tc);
        Assert.Equal(StatoDtc.Validato, DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(store.DatabasePath).StreamEsiti()).PerDtc.Single().Stato);

        tc.Esecuzioni[0].Condizioni[0].Guasto = null;
        store.Upsert(Path.Combine(_root, "T1.json"), Day.AddMinutes(2), 102, tc);
        Assert.Equal(StatoDtc.DaFare, DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(store.DatabasePath).StreamEsiti()).PerDtc.Single().Stato);
    }

    [Fact]
    public void Migrazione_V9_FallitaFaRollbackDellaVersioneENonPerdeLaProiezione_RipartePulita()
    {
        var tests = Dataset();
        var store = NewStore(tests);
        var reqs = new[] { Req("R1", "A", "B", "C"), Req("R2", "A") };
        var atteso = Fp(DtcAvanzamentoService.Calcola(reqs, tests));
        var db = store.DatabasePath;
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE meta SET value = '8' WHERE key = 'schema_version';
                CREATE TRIGGER fail_dtc_backfill BEFORE INSERT ON test_dtc_operation
                BEGIN SELECT RAISE(ABORT, 'injected guasto migration failure'); END;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Throws<SqliteException>(() => new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache")));

        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var v = c.CreateCommand();
            v.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            Assert.Equal("8", v.ExecuteScalar());   // versione NON avanzata
            v.CommandText = "SELECT COUNT(*) FROM test_dtc_operation;";
            Assert.True(Convert.ToInt64(v.ExecuteScalar()) > 0); // la proiezione precedente non e stata svuotata (rollback)
            v.CommandText = "DROP TRIGGER fail_dtc_backfill;";
            v.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        Assert.Equal(atteso, Fp(DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(riaperto.DatabasePath).StreamEsiti())));
        using var c2 = new SqliteConnection($"Data Source={riaperto.DatabasePath}");
        c2.Open();
        using var v2 = c2.CreateCommand();
        v2.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(), v2.ExecuteScalar());
    }

    [Fact]
    public void Streaming_Annullabile()
    {
        var store = NewStore(Dataset());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LocalDtcQueryService(store.DatabasePath).StreamEsiti(cts.Token).ToList());
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void Benchmark_100kDtc_Ingest_Migrazione_StreamingECalcolo()
    {
        const int test = 20_000, perTest = 5, requisiti = 50;
        var tests = new List<TestCase>(test);
        var perReq = new Dictionary<string, List<string>>();
        var n = 0;
        for (var t = 0; t < test; t++)
        {
            var ops = new List<Condizione>();
            for (var k = 0; k < perTest; k++, n++)
            {
                var req = "R" + (n % requisiti);
                var guasto = $"P{n:D6}";
                if (!perReq.TryGetValue(req, out var l)) perReq[req] = l = new();
                l.Add(guasto);
                ops.Add(Op(req, guasto, n % 2 == 0 ? PassoDtc.Validazione : PassoDtc.Devalidazione, Esito.Conforme, k % 2 == 0 ? "ECM" : null));
            }
            tests.Add(Test("B" + t, t % 24, ops.ToArray()));
        }
        var reqs = perReq.Select(kv => Req(kv.Key, kv.Value.ToArray())).ToList();

        var swIngest = Stopwatch.StartNew();
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in tests) store.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);
        swIngest.Stop();

        var swCalc = Stopwatch.StartNew();
        var r = DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(store.DatabasePath).StreamEsiti());
        swCalc.Stop();
        Assert.Equal(100_000, r.Totale.Totale);
        Assert.Equal(0, r.CelleSenzaAnagrafica);

        var swMem = Stopwatch.StartNew();
        var rm = DtcAvanzamentoService.Calcola(reqs, tests);
        swMem.Stop();
        Assert.Equal(Fp(rm), Fp(r));

        var db = store.DatabasePath;
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TABLE test_dtc_operation; UPDATE meta SET value = '8' WHERE key = 'schema_version';";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var swMig = Stopwatch.StartNew();
        var riaperto = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        swMig.Stop();
        Assert.Equal(Fp(rm), Fp(DtcAvanzamentoService.Calcola(reqs, new LocalDtcQueryService(riaperto.DatabasePath).StreamEsiti())));

        _out.WriteLine($"[bench 100k guasto] {test} test x {perTest} guasto; ingest indice {swIngest.ElapsedMilliseconds} ms; " +
                       $"calcolo da SQLite (streaming) {swCalc.ElapsedMilliseconds} ms; calcolo in memoria {swMem.ElapsedMilliseconds} ms; " +
                       $"migrazione v8->v10 (backfill) {swMig.ElapsedMilliseconds} ms");
        Console.WriteLine($"[bench 100k guasto] ingest {swIngest.ElapsedMilliseconds} ms; sqlite {swCalc.ElapsedMilliseconds} ms; mem {swMem.ElapsedMilliseconds} ms; migrazione {swMig.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Lento")]
    public void Benchmark_CostoIngestIndice_ConESenzaOperazioniDtc()
    {
        const int n = 3000;
        TimeSpan Misura(bool conDtc, string sub)
        {
            var store = new LocalTestIndexStore(Path.Combine(_root, sub, "data"), Path.Combine(_root, sub, "cache"));
            var sw = Stopwatch.StartNew();
            for (var t = 0; t < n; t++)
            {
                var ops = new List<Condizione>();
                for (var k = 0; k < 5; k++)
                    ops.Add(conDtc ? Op("R1", $"P{t}_{k}", PassoDtc.Validazione, Esito.Conforme)
                                   : new Condizione { Funzionalita = "F" + k, Esito = Esito.Conforme });
                var tc = Test("I" + t, 1, ops.ToArray());
                store.Upsert(Path.Combine(_root, sub, tc.Id + ".json"), Day, 100, tc);
            }
            return sw.Elapsed;
        }
        Misura(false, "warm");
        var senza = Misura(false, "senza");
        var con = Misura(true, "con");
        var msg = $"[bench ingest] {n} test x 5 op: senza guasto {senza.TotalMilliseconds:0} ms, con 5 guasto/test {con.TotalMilliseconds:0} ms (rapporto {con.TotalMilliseconds / senza.TotalMilliseconds:0.00})";
        _out.WriteLine(msg); Console.WriteLine(msg);
    }
}
