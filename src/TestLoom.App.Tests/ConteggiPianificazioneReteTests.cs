using TestLoom.App.Models;
using TestLoom.App.Services;
using TestLoom.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

/// <summary>
/// Decisione utente 6.4.8, punto 5: la pianificazione non e' un'operazione (una REV solo pianificata non conta come
/// revisione/esecuzione/test eseguito ne per l'operatore planner) e una chiusura da rete (Origine Rete) non e'
/// attribuita a nessun operatore pur contando sul test/componente/funzionalita'. Parita' C# (modello) / SQL (indice).
/// </summary>
public sealed class ConteggiPianificazioneReteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_PianRete_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Esecuzione Rev(int it, string op, Esito esito, OrigineRevisione origine = OrigineRevisione.Operatore, string componente = "Y") => new()
    {
        Iterazione = it, Data = Day.AddHours(it), Operatore = op, Esito = esito, Origine = origine,
        Condizioni = [new Condizione { Funzionalita = "F", Componente = new EcuSelection(new[] { componente }), Esito = esito }]
    };

    private static TestCase Test(string id, params Esecuzione[] revs)
    {
        var tc = new TestCase
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = Day, UltimaModifica = Day, CreatoDa = "Plan", ModificatoDa = "Plan",
            Componente = new EcuSelection(new[] { "Y" }), Funzionalita = ["F"], Veicoli = ["Daily"], Veicolo = "Daily"
        };
        tc.Esecuzioni.AddRange(revs);
        return tc;
    }

    private LocalTestIndexStore NewStore(params TestCase[] tests)
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        foreach (var tc in tests) store.Upsert(Path.Combine(_root, tc.Id + ".json"), Day, 100, tc);
        return store;
    }

    // ---------------------------------------------------------------- modello C#

    [Fact]
    public void EsecuzioneEseguita_SoloConCondizioniRealmenteEseguite()
    {
        Assert.False(OperationalSemantics.EsecuzioneEseguita(Rev(1, "Plan", Esito.DaEseguire)));
        Assert.False(OperationalSemantics.EsecuzioneEseguita(new Esecuzione { Esito = Esito.Conforme }));
        Assert.False(OperationalSemantics.EsecuzioneEseguita(Rev(1, "Plan", Esito.NonApplicabile)));
        Assert.True(OperationalSemantics.EsecuzioneEseguita(Rev(1, "Op", Esito.ConformeConDeviazioni)));
    }

    [Fact]
    public void RevRete_ContaComeEsecuzioneMaNonPerOperatore()
    {
        var rete = Rev(2, "Paolo", Esito.Conforme, OrigineRevisione.Rete);
        Assert.True(OperationalSemantics.EsecuzioneEseguita(rete));
        Assert.False(OperationalSemantics.EsecuzioneAttribuibileAOperatore(rete));
        Assert.True(OperationalSemantics.EsecuzioneAttribuibileAOperatore(Rev(2, "Paolo", Esito.Conforme, OrigineRevisione.RieseguitoDaNoi)));
        Assert.Equal(OrigineRevisione.Rete.Label(), rete.OperatoreVisualizzato);
        Assert.Equal("Paolo", Rev(1, "Paolo", Esito.Conforme).OperatoreVisualizzato);
    }

    [Fact]
    public void TestSoloPianificato_NonTestato_ZeroRevisioniEseguite_NonNeiKpiIterazioni()
    {
        var plan = Test("P", Rev(1, "Plan", Esito.DaEseguire));
        Assert.False(OperationalSemantics.TestTestato(plan));
        Assert.Equal(0, OperationalSemantics.RevisioniEseguite(plan));
        Assert.Null(OperationalSemantics.StatoCorrenteEseguito(plan));
        Assert.Null(KpiService.IterazioniPerConforme(plan));
        var kpi = KpiService.CalcolaAggregato([plan]);
        Assert.Equal(0, kpi.CasiRaggiuntiConforme);
        Assert.Equal(0, kpi.CasiMaiRaggiuntiConforme);
        Assert.Empty(HomeViewModel.ContaEsecuzioniPerOperatore([plan]));
    }

    [Fact]
    public void KpiIterazioni_ContaSoloLeRevEseguite()
    {
        // pianificata + eseguita Conforme = 1 iterazione (non 2)
        var tc = Test("K", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Op", Esito.Conforme));
        Assert.Equal(1, KpiService.IterazioniPerConforme(tc));
    }

    [Fact]
    public void ContaPerOperatore_EscludePianificazioneERete()
    {
        var tc = Test("C", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.NonConforme), Rev(3, "Mario", Esito.Conforme, OrigineRevisione.Rete),
            Rev(4, "Mario", Esito.Conforme, OrigineRevisione.RieseguitoDaNoi));
        var counts = HomeViewModel.ContaEsecuzioniPerOperatore([tc]);
        Assert.Equal(2, counts["Mario"]);
        Assert.DoesNotContain("Plan", counts.Keys);
    }

    [Fact]
    public void DashboardFilterOperatore_NonTrovaPlannerNeRete()
    {
        var tc = Test("F", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.NonConforme), Rev(3, "Rete Op", Esito.Conforme, OrigineRevisione.Rete));
        string? Gamma(string _) => null;
        Assert.False(new DashboardFilter { Operatore = ["Plan"] }.Corrisponde(tc, Gamma));
        Assert.False(new DashboardFilter { Operatore = ["Rete Op"] }.Corrisponde(tc, Gamma));
        Assert.True(new DashboardFilter { Operatore = ["Mario"] }.Corrisponde(tc, Gamma));
        Assert.Empty(new DashboardFilter { Operatore = ["Plan"] }.Applica([tc], Gamma));
        Assert.Empty(new DashboardFilter { Operatore = ["Rete Op"] }.Applica([tc], Gamma));
        Assert.Single(new DashboardFilter { Operatore = ["Mario"] }.Applica([tc], Gamma));
    }

    [Fact]
    public void RevRete_AggiornaCoperturaEsitoDelTest_ConZeroOperazioniPerOperatore()
    {
        var tc = Test("R", Rev(1, "Mario", Esito.NonConforme), Rev(2, "Mario", Esito.Conforme, OrigineRevisione.Rete));
        Assert.Equal(Esito.Conforme, tc.StatoCorrente);
        Assert.Equal(2, OperationalSemantics.RevisioniEseguite(tc));
        Assert.Equal(2, OperationalSemantics.CountAttemptUnits(tc));
        Assert.Equal(2, KpiService.IterazioniPerConforme(tc));
        Assert.Equal(1, HomeViewModel.ContaEsecuzioniPerOperatore([tc])["Mario"]);
    }

    [Fact]
    public void JsonLegacySenzaOrigine_LeggeOperatoreEContaPerOperatore()
    {
        var tc = Test("L", Rev(1, "Mario", Esito.Conforme));
        var json = System.Text.Json.JsonSerializer.Serialize(tc, JsonIo.CompactOptions);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var e in node["Esecuzioni"]!.AsArray()) e!.AsObject().Remove("Origine");
        var back = JsonIo.TryParse<TestCase>(node.ToJsonString(), out _)!;
        Assert.Equal(OrigineRevisione.Operatore, back.Esecuzioni[0].Origine);
        Assert.Equal(1, HomeViewModel.ContaEsecuzioniPerOperatore([back])["Mario"]);
    }

    // ---------------------------------------------------------------- indice SQL

    [Fact]
    public void Sql_GetOperatorOperationStatistics_EscludePianificazioneERete()
    {
        var store = NewStore(
            Test("A", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.Conforme)),
            Test("B", Rev(1, "Plan", Esito.DaEseguire)),
            Test("C", Rev(1, "Mario", Esito.NonConforme), Rev(2, "Mario", Esito.Conforme, OrigineRevisione.Rete)));

        var stats = store.GetOperatorOperationStatistics();
        var mario = Assert.Single(stats);
        Assert.Equal("Mario", mario.Operator);
        Assert.Equal(2, mario.TestCount);
        Assert.Equal(2, mario.RevisionCount);
        Assert.Equal(2, mario.OperationAttemptCount);
        Assert.Equal(1, mario.ConformingRevisionCount);
    }

    [Fact]
    public void Sql_ArchiveStatistics_PlanOnlyNonTestato_ReteContaSuTest()
    {
        var store = NewStore(
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Mario", Esito.NonConforme), Rev(2, "Mario", Esito.Conforme, OrigineRevisione.Rete)));

        var s = store.GetArchiveStatistics();
        Assert.Equal(2, s.TestCount);
        Assert.Equal(2, s.RevisionCount);          // solo le REV eseguite (non la pianificata di A)
        Assert.Equal(1, s.TestedCount);
        Assert.Equal(1, s.ConformingCount);        // la REV Rete ha portato B a Conforme
        Assert.Equal(2, s.OperationAttemptCount);  // l'operazione di rete conta sul test

        var service = new LocalOperationalStatisticsService(store.DatabasePath);
        var o = service.GetArchiveStatistics();
        Assert.Equal(2, o.RevisionCount);
        Assert.Equal(1, o.TestedCount);
        Assert.Equal(1, o.ConformingCount);
        Assert.Equal(2, o.OperationAttemptCount);

        var byEcu = Assert.Single(service.GetByEcu());
        Assert.Equal(2, byEcu.OperationAttemptCount);
        var byOp = Assert.Single(service.GetByOperator());
        Assert.Equal("Mario", byOp.Operator);
        Assert.Equal(1, byOp.OperationAttemptCount);
        Assert.Equal(1, byOp.RevisionCount);
        Assert.Equal(0, byOp.ConformingRevisionCount);
    }

    [Fact]
    public void Sql_OperationalService_RetePura_NessunOperatore()
    {
        var store = NewStore(Test("A", Rev(1, "Mario", Esito.Conforme, OrigineRevisione.Rete)));
        var service = new LocalOperationalStatisticsService(store.DatabasePath);
        Assert.Empty(service.GetByOperator());
        Assert.Empty(store.GetOperatorOperationStatistics());
        Assert.Equal(1, service.GetArchiveStatistics().OperationAttemptCount);
    }

    [Fact]
    public void Sql_HomeSnapshot_PianificazioneNonInKpiNeInOperatoriNeInFiltroOperatore()
    {
        var store = NewStore(
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.Conforme)),
            Test("C", Rev(1, "Mario", Esito.NonConforme), Rev(2, "Net", Esito.Conforme, OrigineRevisione.Rete)));
        var service = new LocalHomeDashboardQueryService(store.DatabasePath);

        var all = service.GetSnapshot(null, null);
        Assert.Equal(2, all.ExecutionCountsByOperator["Mario"]);
        Assert.DoesNotContain("Plan", all.ExecutionCountsByOperator.Keys);
        Assert.DoesNotContain("Net", all.ExecutionCountsByOperator.Keys);
        // KPI iterazioni: A (solo pianificato) non e' ne' raggiunto ne' mai-raggiunto; B = 1 iterazione; C = 2.
        Assert.Equal(2, all.IterationKpi.CasiRaggiuntiConforme);
        Assert.Equal(0, all.IterationKpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(1.5d, all.IterationKpi.MediaIterazioni);

        // Parita' con il percorso C#.
        var tests = new[]
        {
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.Conforme)),
            Test("C", Rev(1, "Mario", Esito.NonConforme), Rev(2, "Net", Esito.Conforme, OrigineRevisione.Rete))
        };
        var kpi = KpiService.CalcolaAggregato(tests);
        Assert.Equal(all.IterationKpi.CasiRaggiuntiConforme, kpi.CasiRaggiuntiConforme);
        Assert.Equal(all.IterationKpi.CasiMaiRaggiuntiConforme, kpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(all.IterationKpi.MediaIterazioni, kpi.MediaIterazioni);

        long testNelFiltro(string op) => service.GetSnapshot(new DashboardFilter { Operatore = [op] }).OutcomeCounts.Values.Sum();
        Assert.Equal(0, testNelFiltro("Plan"));
        Assert.Equal(0, testNelFiltro("Net"));
        Assert.Equal(2, testNelFiltro("Mario"));
    }

    [Fact]
    public void Sql_HomeSummary_TestSoloPianificatoNonTestato()
    {
        var store = NewStore(
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Mario", Esito.Conforme)));
        var summary = new LocalTestArchiveQueryService(store.DatabasePath).GetHomeSummary("Mario");
        Assert.Equal(2, summary.TotalTests);
        Assert.Equal(1, summary.Tested);
        Assert.Equal(1, summary.Conforming);
        Assert.Equal(100.0, summary.ConformityRatePercent);
    }

    [Fact]
    public void Migrazione_IndiceV7_RigeneraColonneExecutedERevisioneOrigine()
    {
        var tests = new[]
        {
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.NonConforme), Rev(3, "Net", Esito.Conforme, OrigineRevisione.Rete))
        };
        var store = NewStore(tests);
        var dbPath = store.DatabasePath;
        SqliteConnection.ClearAllPools();

        // Riporta l'indice allo stato v7: senza le colonne nuove, schema_version = 7.
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                DROP INDEX IF EXISTS ix_test_summary_home_v8;
                ALTER TABLE test_summary DROP COLUMN executed_revision_count;
                ALTER TABLE test_revision DROP COLUMN origine;
                UPDATE meta SET value = '7' WHERE key = 'schema_version';
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var reopened = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        // Il primo accesso applica la migrazione (ctor o prima query).
        var stats = reopened.GetOperatorOperationStatistics();
        var mario = Assert.Single(stats);
        Assert.Equal("Mario", mario.Operator);
        Assert.Equal(1, mario.RevisionCount);
        var archive = reopened.GetArchiveStatistics();
        Assert.Equal(2, archive.RevisionCount);
        Assert.Equal(1, archive.TestedCount);
        Assert.Equal(1, archive.ConformingCount);
    }

    [Fact]
    public void Migrazione_IndiceV7_ServiziDiLetturaHomeFunzionanoDopoLaMigrazioneDelloStore()
    {
        var tests = new[]
        {
            Test("A", Rev(1, "Plan", Esito.DaEseguire)),
            Test("B", Rev(1, "Plan", Esito.DaEseguire), Rev(2, "Mario", Esito.NonConforme), Rev(3, "Net", Esito.Conforme, OrigineRevisione.Rete))
        };
        var store = NewStore(tests);
        var dbPath = store.DatabasePath;
        SqliteConnection.ClearAllPools();

        // Indice utente v7 reale: schema precedente senza executed_revision_count / origine.
        using (var c = new SqliteConnection($"Data Source={dbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                DROP INDEX IF EXISTS ix_test_summary_home_v8;
                ALTER TABLE test_summary DROP COLUMN executed_revision_count;
                ALTER TABLE test_revision DROP COLUMN origine;
                UPDATE meta SET value = '7' WHERE key = 'schema_version';
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Unico punto di apertura dell'indice: il costruttore dello store (da cui i servizi ricavano il percorso).
        var reopened = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));

        var snapshot = new LocalHomeDashboardQueryService(reopened.DatabasePath).GetSnapshot(null, null);
        Assert.Equal(1L, snapshot.ExecutionCountsByOperator["Mario"]);
        Assert.DoesNotContain("Plan", snapshot.ExecutionCountsByOperator.Keys);
        Assert.DoesNotContain("Net", snapshot.ExecutionCountsByOperator.Keys);

        var summary = new LocalTestArchiveQueryService(reopened.DatabasePath).GetHomeSummary("Mario");
        Assert.Equal(2, summary.TotalTests);
        Assert.Equal(1, summary.Tested);
        Assert.Equal(1, summary.Conforming);
    }
}
