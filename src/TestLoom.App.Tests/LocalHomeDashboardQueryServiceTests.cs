using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class LocalHomeDashboardQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_HomeQueries_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Snapshot_RespectsInclusiveDatesDeletionAndEveryExecution_ThenSeesUpdates()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var day = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        TestCase Make(string id, DateTime date, bool deleted = false) => new()
        {
            Id = id, Uid = Guid.NewGuid(), DataCreazione = date, Eliminato = deleted,
            Veicoli = ["Daily"], Veicolo = "Daily"
        };
        var first = Make("A", day);
        first.Esecuzioni.Add(new Esecuzione { Iterazione = 1, Data = day, Operatore = "Mario", Esito = Esito.NonConforme, Condizioni = [new Condizione { Esito = Esito.NonConforme }] });
        first.Esecuzioni.Add(new Esecuzione { Iterazione = 2, Data = day.AddHours(1), Operatore = "mario", Esito = Esito.Conforme, Condizioni = [new Condizione { Esito = Esito.Conforme }] });
        var last = Make("B", day.AddDays(1));
        var outside = Make("C", day.AddTicks(-1));
        var deleted = Make("D", day, true);
        foreach (var tc in new[] { first, last, outside, deleted })
            store.Upsert(Path.Combine(_root, tc.Id + ".json"), day, 100, tc);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var snapshot = service.GetSnapshot(day, day.AddDays(1));
        Assert.Equal(2, snapshot.VehicleCounts["Daily"]);
        Assert.Equal(2, snapshot.ExecutionCountsByOperator["Mario"]);
        Assert.Equal(2, snapshot.OutcomeCounts.Values.Sum());
        Assert.Equal(1, snapshot.OutcomeCounts[Esito.Conforme]);
        Assert.Equal(1, snapshot.OutcomeCounts[Esito.NonEseguitoNA]);
        Assert.Equal(1, snapshot.IterationKpi.CasiRaggiuntiConforme);
        Assert.Equal(0, snapshot.IterationKpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(2d, snapshot.IterationKpi.MediaIterazioni);
        Assert.Equal(3, service.GetSnapshot(null, null).OutcomeCounts.Values.Sum());
        Assert.Empty(service.GetSnapshot(day.AddDays(2), null).OutcomeCounts);

        first.Eliminato = true;
        store.Upsert(Path.Combine(_root, "A.json"), day.AddDays(2), 101, first);
        snapshot = service.GetSnapshot(day, day.AddDays(1));
        Assert.Equal(1, snapshot.VehicleCounts["Daily"]);
        Assert.Empty(snapshot.ExecutionCountsByOperator);
        Assert.Equal(0, snapshot.IterationKpi.MediaIterazioni);
        first.Eliminato = false;
        store.Upsert(Path.Combine(_root, "A.json"), day.AddDays(3), 102, first);
        Assert.Equal(2, service.GetSnapshot(day, day.AddDays(1)).VehicleCounts["Daily"]);
    }

    [Fact]
    public void Snapshot_UsesKnownNoRevisionCountOnlyForUnfilteredDashboard()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var day = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        var withVehicle = new TestCase { Id = "A", Uid = Guid.NewGuid(), DataCreazione = day, Veicoli = ["Daily"], Veicolo = "Daily" };
        var withoutVehicle = new TestCase { Id = "B", Uid = Guid.NewGuid(), DataCreazione = day };
        var deleted = new TestCase { Id = "C", Uid = Guid.NewGuid(), DataCreazione = day, Eliminato = true };
        foreach (var test in new[] { withVehicle, withoutVehicle, deleted })
            store.Upsert(Path.Combine(_root, test.Id + ".json"), day, 100, test);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var reference = service.GetSnapshot(new DashboardFilter());
        var batch = new LocalTestArchiveQueryService(store.DatabasePath).GetHomeAggiornaBatch(new() { IncludeEcu = false });
        Assert.False(batch.HasAnyRevision);
        var reused = service.GetSnapshot(new DashboardFilter(), knownNonExecutedTestCount: batch.Summary.TotalTests,
            knownNonExecutedRevisione: batch.Revisione);

        Assert.Equal(reference.OutcomeCounts, reused.OutcomeCounts);
        Assert.Equal(reference.VehicleCounts, reused.VehicleCounts);
        Assert.Equal(reference.VehicleOutcomeRows, reused.VehicleOutcomeRows);
        Assert.Equal(reference.IterationKpi.MediaIterazioni, reused.IterationKpi.MediaIterazioni);
        Assert.Equal(reference.IterationKpi.CasiRaggiuntiConforme, reused.IterationKpi.CasiRaggiuntiConforme);
        Assert.Equal(reference.IterationKpi.CasiMaiRaggiuntiConforme, reused.IterationKpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(reference.ExecutionCountsByOperator, reused.ExecutionCountsByOperator);

        var filteredReference = service.GetSnapshot(new DashboardFilter { Veicolo = ["Daily"] });
        var filteredWithHint = service.GetSnapshot(new DashboardFilter { Veicolo = ["Daily"] },
            knownNonExecutedTestCount: batch.Summary.TotalTests, knownNonExecutedRevisione: batch.Revisione);
        Assert.Equal(filteredReference.OutcomeCounts, filteredWithHint.OutcomeCounts);
        Assert.Single(filteredWithHint.OutcomeCounts);
        Assert.Equal(1, filteredWithHint.OutcomeCounts.Values.Sum());
    }

    [Fact]
    public void Snapshot_ExecutionCountsByOperator_CountsOnlyRevisionsWithAnExecutedOperation()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var day = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        Esecuzione Rev(int it, string op, params Esito[] esiti) => new()
        {
            Iterazione = it, Data = day.AddHours(it), Operatore = op,
            Esito = esiti.Length > 0 ? esiti[0] : Esito.DaEseguire,
            Condizioni = esiti.Select(e => new Condizione { Esito = e }).ToList()
        };
        var tc = new TestCase { Id = "OP", Uid = Guid.NewGuid(), DataCreazione = day, Veicoli = ["Daily"], Veicolo = "Daily" };
        tc.Esecuzioni.Add(Rev(1, "Solo NA", Esito.NonApplicabile, Esito.NonEseguitoNA));
        tc.Esecuzioni.Add(Rev(2, "Solo NA", Esito.DaEseguire, Esito.DaRivedere));
        tc.Esecuzioni.Add(Rev(3, "Misto", Esito.NonApplicabile, Esito.Conforme));
        tc.Esecuzioni.Add(Rev(4, "Misto", Esito.NonApplicabile));
        tc.Esecuzioni.Add(Rev(5, "Nessuna condizione"));
        store.Upsert(Path.Combine(_root, "OP.json"), day, 100, tc);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var counts = service.GetSnapshot(null, null).ExecutionCountsByOperator;
        Assert.Equal(1, counts["Misto"]);
        Assert.DoesNotContain("Solo NA", counts.Keys);
        Assert.DoesNotContain("Nessuna condizione", counts.Keys);
        // Il percorso non-SQL (HomeViewModel) deve dare lo stesso risultato.
        Assert.Equal(
            counts.OrderBy(x => x.Key).ToList(),
            TestLoom.App.ViewModels.HomeViewModel.ContaEsecuzioniPerOperatore([tc]).OrderBy(x => x.Key).ToList());
    }

    [Fact]
    public void Snapshot_WithEveryDashboardDimension_MatchesDashboardFilter()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var day = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var templateA = Guid.NewGuid();
        var templateB = Guid.NewGuid();
        TestCase Make(string id, string vehicle, string componente, string functionality, string requirement, string instrument,
            string assignee, Guid template, Esito? outcome, string custom, string operatorName) => new()
            {
                Id = id, Uid = Guid.NewGuid(), DataCreazione = day, Veicolo = vehicle, Veicoli = [vehicle],
                Componente = [componente], Funzionalita = [functionality], RequisitoId = [requirement], StrumentiUsati = [instrument],
                Assegnatario = assignee, TestTemplateUid = template, CampiCustom = new() { ["Colore"] = custom },
                Esecuzioni = outcome.HasValue
                    ? [new Esecuzione { Iterazione = 1, Data = day, Operatore = operatorName, Esito = outcome.Value, Condizioni = [new Condizione { Esito = outcome.Value }] }]
                    : []
            };

        var almost = Make("A", "Veicolo-A", "Componente-A", "Funzione-A", "REQ-A", "Strumento-A", "Mario", templateA, Esito.Conforme, "Rosso", "Mario");
        var other = Make("B", "Veicolo-B", "Componente-B", "Funzione-B", "REQ-B", "Strumento-B", "Luisa", templateB, Esito.NonConforme, "Blu", "Luisa");
        var unexecuted = Make("C", "Veicolo-C", "Componente-C", "Funzione-C", "REQ-C", "Strumento-C", "Pietro", templateB, null, "Verde", "Pietro");
        foreach (var tc in new[] { almost, other, unexecuted }) store.Upsert(Path.Combine(_root, tc.Id + ".json"), day, 100, tc);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var all = new[] { almost, other, unexecuted };
        var gammaByVehicle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Veicolo-A"] = "Gamma-A", ["Veicolo-B"] = "Gamma-B", ["Veicolo-C"] = "Gamma-C"
        };
        var categoriesByFunctionality = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Funzione-A"] = ["Categoria-A"], ["Funzione-B"] = ["Categoria-B"], ["Funzione-C"] = ["Categoria-C"]
        };

        var filters = new DashboardFilter[]
        {
            new() { Gamma = ["Gamma-A"] }, new() { Veicolo = ["Veicolo-A"] }, new() { Componente = ["Componente-A"] },
            new() { Funzionalita = ["Funzione-A"] }, new() { CategoriaFunzionalita = ["Categoria-A"] },
            new() { Requisito = ["REQ-A"] }, new() { Strumento = ["Strumento-A"] }, new() { Operatore = ["Mario"] },
            new() { Assegnatario = ["Mario"] }, new() { TestTemplateUid = [templateA] }, new() { Esito = [Esito.Conforme] },
            new() { CampiCustom = new() { ["Colore"] = ["Rosso"] } },
            new() { Gamma = ["Gamma-A"], Componente = ["Componente-A"], Strumento = ["Strumento-A"], Operatore = ["Mario"], CampiCustom = new() { ["Colore"] = ["Rosso"] } }
        };

        foreach (var filter in filters)
        {
            var gammaVehicles = filter.Gamma.Count == 0 ? null : gammaByVehicle.Where(x => filter.Gamma.Contains(x.Value)).Select(x => x.Key).ToList();
            var categoryFunctions = filter.CategoriaFunzionalita.Count == 0 ? null : categoriesByFunctionality
                .Where(x => x.Value.Any(filter.CategoriaFunzionalita.Contains)).Select(x => x.Key).ToList();
            var expected = filter.Applica(all, v => gammaByVehicle.GetValueOrDefault(v), f => categoriesByFunctionality.GetValueOrDefault(f) ?? []);
            var snapshot = service.GetSnapshot(filter, gammaVehicles, categoryFunctions);

            Assert.Equal(expected.Count, snapshot.OutcomeCounts.Values.Sum());
            Assert.Equal(
                expected.SelectMany(x => x.VeicoliEffettivi).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key)
                    .Select(x => (x.Key, Count: (long)x.Count())),
                snapshot.VehicleCounts.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)));
            Assert.Equal(
                expected.SelectMany(x => x.Esecuzioni).Where(x => !string.IsNullOrEmpty(x.Operatore) && x.Condizioni.Any(c => c.Esito.EEseguito()))
                    .GroupBy(x => x.Operatore, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key)
                    .Select(x => (x.Key, Count: (long)x.Count())),
                snapshot.ExecutionCountsByOperator.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)));
        }
    }

    [Fact]
    public void InstrumentProjection_MigratesExistingV3Cache()
    {
        var store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var tc = new TestCase
        {
            Id = "STRUMENTO", Uid = Guid.NewGuid(), DataCreazione = DateTime.UtcNow,
            Veicolo = "Veicolo", Veicoli = ["Veicolo"], StrumentiUsati = ["ValidationTool"]
        };
        store.Upsert(Path.Combine(_root, "strumento.json"), DateTime.UtcNow, 100, tc);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE test_instrument; UPDATE meta SET value = '3' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        store = new LocalTestIndexStore(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var snapshot = new LocalHomeDashboardQueryService(store.DatabasePath)
            .GetSnapshot(new DashboardFilter { Strumento = ["ValidationTool"] });

        Assert.Equal(1, snapshot.OutcomeCounts.Values.Sum());
        Assert.Equal(1, snapshot.VehicleCounts["Veicolo"]);
    }

    [Fact]
    public void ProjectionMigration_V2FailureRollsBackVersionAndBothBackfills_ThenRestartsCleanly()
    {
        var data = Path.Combine(_root, "data");
        var cache = Path.Combine(_root, "cache");
        var store = new LocalTestIndexStore(data, cache);
        var tc = new TestCase
        {
            Id = "MIGRAZIONE", Uid = Guid.NewGuid(), DataCreazione = DateTime.UtcNow,
            Veicolo = "Veicolo", Veicoli = ["Veicolo"], StrumentiUsati = ["ValidationTool"],
            CampiCustom = new() { ["Fleet"] = "Alpha" }
        };
        store.Upsert(Path.Combine(data, "migrazione.json"), DateTime.UtcNow, 100, tc);

        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM test_custom_field;
                DELETE FROM test_instrument;
                UPDATE meta SET value = '2' WHERE key = 'schema_version';
                CREATE TRIGGER fail_instrument_backfill
                BEFORE INSERT ON test_instrument
                BEGIN
                    SELECT RAISE(ABORT, 'injected instrument migration failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<SqliteException>(() => new LocalTestIndexStore(data, cache));

        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            Assert.Equal("2", verify.ExecuteScalar());
            verify.CommandText = "SELECT COUNT(*) FROM test_custom_field;";
            Assert.Equal(0L, Convert.ToInt64(verify.ExecuteScalar()));
            verify.CommandText = "SELECT COUNT(*) FROM test_instrument;";
            Assert.Equal(0L, Convert.ToInt64(verify.ExecuteScalar()));
            verify.CommandText = "DROP TRIGGER fail_instrument_backfill;";
            verify.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        store = new LocalTestIndexStore(data, cache);
        var query = new LocalTestArchiveQueryService(store.DatabasePath);
        Assert.Equal(new[] { "Alpha" }, query.GetCustomFieldValues("Fleet"));
        var snapshot = new LocalHomeDashboardQueryService(store.DatabasePath)
            .GetSnapshot(new DashboardFilter { Strumento = ["ValidationTool"] });
        Assert.Equal(1, snapshot.OutcomeCounts.Values.Sum());

        using var reopened = new SqliteConnection($"Data Source={store.DatabasePath}");
        reopened.Open();
        using var schema = reopened.CreateCommand();
        schema.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        // Versione corrente dell'indice locale (LocalTestIndexStore.CurrentSchemaVersion), non TestCase.CurrentSchemaVersion:
        // i due schemi sono numerati indipendentemente. RecomputeOperationCounts (v5->v6) + backfill origine_test (v6->v7,
        // piano 6.4.0 "Test importati: solo stato finale") seguono subito dopo la migrazione v2->v5.
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), schema.ExecuteScalar());
    }

    [Fact]
    public void ProjectionMigration_V4RawDateFailureRollsBackSchemaAndRestartsCleanly()
    {
        var data = Path.Combine(_root, "data-v5");
        var cache = Path.Combine(_root, "cache-v5");
        var store = new LocalTestIndexStore(data, cache);
        var rawDate = new DateTime(2026, 6, 1, 0, 30, 0, DateTimeKind.Unspecified);
        var tc = new TestCase
        {
            Id = "RAW-DATE", Uid = Guid.NewGuid(), DataCreazione = rawDate,
            Veicolo = "Veicolo", Veicoli = ["Veicolo"], StrumentiUsati = ["ValidationTool"]
        };
        store.Upsert(Path.Combine(data, "raw-date.json"), DateTime.UtcNow, 100, tc);

        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX ix_test_summary_created_raw;
                ALTER TABLE test_summary DROP COLUMN data_creazione_raw_ticks;
                UPDATE meta SET value = '4' WHERE key = 'schema_version';
                CREATE TRIGGER fail_raw_date_backfill
                BEFORE UPDATE ON test_summary
                BEGIN
                    SELECT RAISE(ABORT, 'injected raw date migration failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<SqliteException>(() => new LocalTestIndexStore(data, cache));

        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
            Assert.Equal("4", verify.ExecuteScalar());
            verify.CommandText = "SELECT COUNT(*) FROM pragma_table_info('test_summary') WHERE name='data_creazione_raw_ticks';";
            Assert.Equal(0L, Convert.ToInt64(verify.ExecuteScalar()));
            verify.CommandText = "SELECT COUNT(*) FROM test_instrument WHERE value='ValidationTool';";
            Assert.Equal(1L, Convert.ToInt64(verify.ExecuteScalar()));
            verify.CommandText = "DROP TRIGGER fail_raw_date_backfill;";
            verify.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        store = new LocalTestIndexStore(data, cache);
        using var reopened = new SqliteConnection($"Data Source={store.DatabasePath}");
        reopened.Open();
        using var schema = reopened.CreateCommand();
        schema.CommandText = "SELECT value FROM meta WHERE key='schema_version';";
        // Versione corrente dell'indice: vedi il commento gemello in ProjectionMigration_V2FailureRollsBackVersionAndBothBackfills_ThenRestartsCleanly.
        Assert.Equal(LocalTestIndexStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), schema.ExecuteScalar());
        schema.CommandText = "SELECT data_creazione_raw_ticks FROM test_summary WHERE uid=$uid;";
        schema.Parameters.AddWithValue("$uid", tc.Uid.ToString("N"));
        Assert.Equal(rawDate.Ticks, Convert.ToInt64(schema.ExecuteScalar()));
        schema.Parameters.Clear();
        schema.CommandText = "SELECT COUNT(*) FROM pragma_index_list('test_summary') WHERE name='ix_test_summary_created_raw';";
        Assert.Equal(1L, Convert.ToInt64(schema.ExecuteScalar()));
        schema.CommandText = "SELECT COUNT(*) FROM test_instrument WHERE value='ValidationTool';";
        Assert.Equal(1L, Convert.ToInt64(schema.ExecuteScalar()));
    }

    [Fact]
    public void RequirementCoverage_WithoutRevisioni_NonClassificaAssociamentiComeEsiti()
    {
        var data = Path.Combine(_root, "data-no-revisions");
        var store = new LocalTestIndexStore(data, Path.Combine(_root, "cache-no-revisions"));
        var standard = new Requisito { Id = "REQ-STD", ApplicabileBanco = true, ApplicabileVeicolo = false };
        var extra = new Requisito { Id = "REQ-Extra", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, ["Daily"]);
        var test = new TestCase
        {
            Id = "TC-PLANNED", Uid = Guid.NewGuid(), DataCreazione = DateTime.UtcNow,
            Veicolo = "Daily", Veicoli = ["Daily"], RequisitoId = [standard.Id, extra.Id]
        };
        store.Upsert(Path.Combine(data, "planned.json"), DateTime.UtcNow, 100, test);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var queryReads = 0;
        service.RequirementCoverageStandardReadObserver = () => queryReads++;
        var coverage = service.GetRequirementCoverage([standard, extra], new DashboardFilter());

        Assert.Equal(2, coverage.NonTestati);
        Assert.Equal(0, coverage.PercentualeCompletamento);
        Assert.Equal(0, queryReads);
    }

    [Fact]
    public void RequirementCoverage_ConcurrentWriter_DoesNotMixStandardAndD4DSnapshots()
    {
        var data = Path.Combine(_root, "data");
        var cache = Path.Combine(_root, "cache");
        var store = new LocalTestIndexStore(data, cache);
        var now = DateTime.UtcNow;
        var requisito = new Requisito { Id = "REQ-HOME-Extra", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, ["Daily"]);

        TestCase Crea(string id, DateTime dataEsecuzione, Esito esito) => new()
        {
            Id = id,
            Uid = Guid.NewGuid(),
            DataCreazione = dataEsecuzione.AddDays(-1),
            UltimaModifica = dataEsecuzione,
            Veicolo = "Daily",
            Veicoli = ["Daily"],
            RequisitoId = [requisito.Id],
            Esecuzioni = [new Esecuzione { Data = dataEsecuzione, Esito = esito }]
        };

        var precedente = Crea("HOME-OLD", now, Esito.NonConforme);
        store.Upsert(Path.Combine(data, "home_old.json"), now, 100, precedente);

        var service = new LocalHomeDashboardQueryService(store.DatabasePath);
        var writerExecuted = false;
        service.RequirementCoverageStandardReadObserver = () =>
        {
            writerExecuted = true;
            var nuovo = Crea("HOME-NEW", now.AddMinutes(1), Esito.Conforme);
            store.Upsert(Path.Combine(data, "home_new.json"), now.AddMinutes(1), 100, nuovo);
        };

        var snapshot = service.GetRequirementCoverage([requisito], new DashboardFilter());

        Assert.True(writerExecuted);
        Assert.Equal(1, snapshot.NonConformi);
        Assert.Equal(0, snapshot.Conformi);
        Assert.Equal(1, new LocalHomeDashboardQueryService(store.DatabasePath)
            .GetRequirementCoverage([requisito], new DashboardFilter()).Conformi);
    }

    [Fact]
    public void Snapshot_IndiciESommaOrdinatiUgualiAlleQueryOriginali_ConRevisioni()
    {
        var giorno = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        var store = new LocalTestIndexStore(Path.Combine(_root, "dati-indici"), Path.Combine(_root, "cache-indici"));
        CaricaArchivioCasuale(store, new Random(20261012), giorno, dimensione: 400, conEsecuzioni: true);

        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        var filtri = new[] { new DashboardFilter(), new DashboardFilter { DataDa = giorno.AddDays(-2), DataA = giorno.AddDays(1) } };
        foreach (var filtro in filtri)
            AssertSnapshotUgualeAlRiferimento(SnapshotDiRiferimentoSql(store.DatabasePath, filtro), servizio.GetSnapshot(filtro));

        // Con revisioni la query degli operatori resta: nessun salto.
        Assert.Equal(0, servizio.OperatoriSaltati);
    }

    [Fact]
    public void Snapshot_SenzaRevisioni_SaltaLaQueryDegliOperatoriSenzaCambiareIlRisultato()
    {
        var giorno = new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
        var store = new LocalTestIndexStore(Path.Combine(_root, "dati-senza-revisioni"), Path.Combine(_root, "cache-senza-revisioni"));
        CaricaArchivioCasuale(store, new Random(7), giorno, dimensione: 200, conEsecuzioni: false);

        var servizio = new LocalHomeDashboardQueryService(store.DatabasePath);
        var filtro = new DashboardFilter { DataDa = giorno.AddDays(-2), DataA = giorno.AddDays(1) };
        var ottenuto = servizio.GetSnapshot(filtro);

        Assert.Equal(1, servizio.OperatoriSaltati);
        Assert.Empty(ottenuto.ExecutionCountsByOperator);
        AssertSnapshotUgualeAlRiferimento(SnapshotDiRiferimentoSql(store.DatabasePath, filtro), ottenuto);
    }

    /// <summary>Stesso snapshot calcolato con le query SQL originali, senza indici forzati, ordinamenti aggiunti o salti.</summary>
    private static LocalDefaultDashboardSnapshot SnapshotDiRiferimentoSql(string percorsoDatabase, DashboardFilter filtro)
    {
        var clausole = new List<string> { "s.eliminato = 0", "s.origine_test = 0" };
        var parametri = new Dictionary<string, object>();
        if (filtro.DataDa.HasValue)
        {
            clausole.Add("s.data_creazione_ticks >= $homeDateFrom");
            parametri["homeDateFrom"] = filtro.DataDa.Value.ToUniversalTime().Ticks;
        }
        if (filtro.DataA.HasValue)
        {
            clausole.Add("s.data_creazione_ticks < $homeDateTo");
            parametri["homeDateTo"] = DataAInclusiva.FineEsclusa(filtro.DataA.Value).ToUniversalTime().Ticks;
        }
        var where = "WHERE " + string.Join(" AND ", clausole);
        var filterOn = "AND " + string.Join(" AND ", clausole);
        var snapshot = new LocalDefaultDashboardSnapshot();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = percorsoDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT v.value, COALESCE(s.current_outcome, $notExecuted), COUNT(*)
                FROM test_vehicle v
                JOIN test_summary s ON s.uid = v.test_uid {filterOn}
                GROUP BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted)
                ORDER BY v.value COLLATE NOCASE, COALESCE(s.current_outcome, $notExecuted);
                """;
            foreach (var (nome, valore) in parametri) command.Parameters.AddWithValue("$" + nome, valore);
            command.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var veicolo = reader.GetString(0);
                var conteggio = reader.GetInt64(2);
                snapshot.VehicleOutcomeRows.Add((veicolo, (Esito)reader.GetInt32(1), conteggio));
                snapshot.VehicleCounts[veicolo] = snapshot.VehicleCounts.GetValueOrDefault(veicolo) + conteggio;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT COALESCE(s.current_outcome, $notExecuted), COUNT(*),
                    SUM(CASE WHEN s.executed_revision_count > 0 AND s.first_conforme_revision IS NOT NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN s.executed_revision_count > 0 AND s.first_conforme_revision IS NULL THEN 1 ELSE 0 END),
                    SUM(CASE WHEN s.first_conforme_revision IS NOT NULL THEN s.first_conforme_revision ELSE 0 END),
                    SUM(CASE WHEN s.first_conforme_revision IS NOT NULL THEN 1 ELSE 0 END)
                FROM test_summary s
                {where}
                GROUP BY COALESCE(s.current_outcome, $notExecuted);
                """;
            foreach (var (nome, valore) in parametri) command.Parameters.AddWithValue("$" + nome, valore);
            command.Parameters.AddWithValue("$notExecuted", (int)Esito.NonEseguitoNA);
            long casiRaggiunti = 0, casiMaiRaggiunti = 0, sommaIterazioni = 0, testConformeConteggiabile = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.OutcomeCounts[(Esito)reader.GetInt32(0)] = reader.GetInt64(1);
                casiRaggiunti += reader.GetInt64(2);
                casiMaiRaggiunti += reader.GetInt64(3);
                sommaIterazioni += reader.GetInt64(4);
                testConformeConteggiabile += reader.GetInt64(5);
            }
            snapshot.IterationKpi = new KpiIterazioniConforme
            {
                MediaIterazioni = testConformeConteggiabile == 0 ? 0 : (double)sommaIterazioni / testConformeConteggiabile,
                CasiRaggiuntiConforme = casiRaggiunti,
                CasiMaiRaggiuntiConforme = casiMaiRaggiunti
            };
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT r.operatore, SUM(r.unit_count)
                FROM test_revision r
                JOIN test_summary s ON s.uid = r.test_uid {filterOn}
                WHERE TRIM(r.operatore) <> '' AND r.raw_attempt_count > 0 AND r.origine <> 1
                GROUP BY r.operatore COLLATE NOCASE
                ORDER BY SUM(r.unit_count) DESC, r.operatore COLLATE NOCASE;
                """;
            foreach (var (nome, valore) in parametri) command.Parameters.AddWithValue("$" + nome, valore);
            using var reader = command.ExecuteReader();
            while (reader.Read()) snapshot.ExecutionCountsByOperator[reader.GetString(0)] = reader.GetInt64(1);
        }
        return snapshot;
    }

    private static void AssertSnapshotUgualeAlRiferimento(LocalDefaultDashboardSnapshot attesa, LocalDefaultDashboardSnapshot ottenuta)
    {
        Assert.Equal(attesa.VehicleCounts.ToList(), ottenuta.VehicleCounts.ToList());
        Assert.Equal(attesa.VehicleOutcomeRows, ottenuta.VehicleOutcomeRows);
        Assert.Equal(attesa.OutcomeCounts.ToList(), ottenuta.OutcomeCounts.ToList());
        Assert.Equal(attesa.ExecutionCountsByOperator.ToList(), ottenuta.ExecutionCountsByOperator.ToList());
        Assert.Equal(attesa.IterationKpi.MediaIterazioni, ottenuta.IterationKpi.MediaIterazioni);
        Assert.Equal(attesa.IterationKpi.CasiRaggiuntiConforme, ottenuta.IterationKpi.CasiRaggiuntiConforme);
        Assert.Equal(attesa.IterationKpi.CasiMaiRaggiuntiConforme, ottenuta.IterationKpi.CasiMaiRaggiuntiConforme);
    }

    private void CaricaArchivioCasuale(LocalTestIndexStore store, Random casuale, DateTime giorno, int dimensione, bool conEsecuzioni)
    {
        string[] veicoli = ["Daily", "VAN-A", "Città-1", "BUS-2", "Bus"];
        var esiti = Enum.GetValues<Esito>();
        for (var i = 0; i < dimensione; i++)
        {
            var tc = new TestCase
            {
                Id = "T" + i.ToString("D4"),
                Uid = Guid.NewGuid(),
                DataCreazione = giorno.AddDays(casuale.Next(-3, 4)).AddHours(casuale.Next(0, 24)),
                Eliminato = casuale.Next(20) == 0,
                Origine = casuale.Next(15) == 0 ? OrigineTest.ImportStatoUdt : OrigineTest.TestLoom,
                Assegnatario = casuale.Next(2) == 0 ? "Mario" : "Luigi",
            };
            var quanti = casuale.Next(0, 3);
            for (var v = 0; v < quanti; v++)
                tc.Veicoli.Add(veicoli[casuale.Next(veicoli.Length)]);
            var numeroEsecuzioni = conEsecuzioni ? casuale.Next(0, 3) : 0;
            for (var e = 0; e < numeroEsecuzioni; e++)
            {
                var esito = esiti[casuale.Next(esiti.Length)];
                tc.Esecuzioni.Add(new Esecuzione
                {
                    Iterazione = e + 1, Data = giorno.AddHours(e), Operatore = "Mario", Esito = esito,
                    Condizioni = [new Condizione { Esito = esito }]
                });
            }
            store.Upsert(Path.Combine(_root, tc.Id + ".json"), giorno, 100 + i, tc);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
