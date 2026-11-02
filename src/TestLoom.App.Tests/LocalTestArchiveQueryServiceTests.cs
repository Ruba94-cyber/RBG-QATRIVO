using TestLoom.App.Models;
using TestLoom.App.Services;
using Microsoft.Data.Sqlite;

namespace TestLoom.App.Tests;

public sealed class LocalTestArchiveQueryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TestLoom_QueryRoot_" + Guid.NewGuid().ToString("N"));
    private readonly string _local = Path.Combine(Path.GetTempPath(), "TestLoom_QueryLocal_" + Guid.NewGuid().ToString("N"));

    public LocalTestArchiveQueryServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_local);
    }

    [Fact]
    public void Query_PaginatesWithoutHydratingWholeArchive_AndKeepsNewestFirst()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 620; i++)
        {
            var tc = NewTest(i, now.AddMinutes(i));
            store.Upsert(Path.Combine(_root, $"testcase_{i:D4}.json"), now.AddMinutes(i), 100 + i, tc);
        }

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var page = service.Query(new LocalTestArchiveQuery { Page = 2, PageSize = 250 });

        Assert.Equal(620, page.TotalCount);
        Assert.Equal(3, page.PageCount);
        Assert.Equal(250, page.Items.Count);
        Assert.Equal("TC-0369", page.Items[0].Id);
        Assert.Equal("TC-0120", page.Items[^1].Id);
    }

    [Fact]
    public void Query_AppliesIndexedFiltersAndVehicleScopeBeforePagination()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 80; i++)
        {
            var tc = NewTest(i, now.AddMinutes(i));
            tc.Componente = i % 2 == 0 ? "ECM" : "BCM";
            tc.Veicoli = [i % 3 == 0 ? "Daily" : "Alpha"];
            tc.Veicolo = tc.Veicoli[0];
            tc.Assegnatario = i % 4 == 0 ? "Mario" : "Luigi";
            tc.RequisitoId = [i % 5 == 0 ? "REQ-A" : "REQ-B"];
            tc.Funzionalita = [i % 7 == 0 ? "Guasto" : "Parameters"];
            store.Upsert(Path.Combine(_root, $"testcase_{i:D4}.json"), now.AddMinutes(i), 200, tc);
        }

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var page = service.Query(new LocalTestArchiveQuery
        {
            Componente = "ECM",
            Assignee = "Mario",
            Requirement = "REQ-A",
            AllowedVehicles = ["Daily"],
            PageSize = 20
        });

        Assert.NotEmpty(page.Items);
        Assert.All(page.Items, tc =>
        {
            Assert.Contains(tc.Componente, x => x.Equals("ECM", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Mario", tc.Assegnatario);
            Assert.Contains("REQ-A", tc.RequisitoId);
            Assert.Contains(tc.VeicoliEffettivi, x => x.Equals("Daily", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Equal(page.TotalCount, page.Items.Count);
    }

    [Fact]
    public void Query_AllowedEcus_FiltersBeforeMaterialization()
    {
        var store = new LocalTestIndexStore(_root, _local);
        for (var i = 0; i < 12; i++)
        {
            var tc = NewTest(i, DateTime.UtcNow.AddMinutes(i));
            tc.Componente = i % 3 == 0 ? "ECM" : i % 3 == 1 ? "BCM" : "TCU";
            store.Upsert(Path.Combine(_root, $"ecu_{i:D3}.json"), DateTime.UtcNow, 100, tc);
        }

        var items = new LocalTestArchiveQueryService(store.DatabasePath).QueryAll(new LocalTestArchiveQuery
        {
            AllowedEcus = ["ecm", "TCU"]
        });

        Assert.Equal(8, items.Count);
        Assert.All(items, tc => Assert.Contains(tc.Componente, componente =>
            componente.Equals("ECM", StringComparison.OrdinalIgnoreCase) ||
            componente.Equals("TCU", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void QueryAll_ConcurrentWriter_KeepsCountAndEveryPageOnOneSnapshot()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 1001; i++)
            store.Upsert(Path.Combine(_root, $"snapshot_{i:D4}.json"), now.AddMinutes(i), 100, NewTest(i, now.AddMinutes(i)));

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var writerExecuted = false;
        service.QueryAllPageReadObserver = offset =>
        {
            if (offset != 0 || writerExecuted) return;
            writerExecuted = true;
            store.Upsert(Path.Combine(_root, "snapshot_new.json"), now.AddDays(10), 100, new TestCase
            {
                Uid = Guid.NewGuid(), Id = "TC-NEW", DataCreazione = now, UltimaModifica = now.AddDays(10)
            });
        };

        var items = service.QueryAll(new LocalTestArchiveQuery(), maximumRows: 2000);

        Assert.True(writerExecuted);
        Assert.Equal(1001, items.Count);
        Assert.Equal(1001, items.Select(x => x.Uid).Distinct().Count());
        Assert.DoesNotContain(items, x => x.Id == "TC-NEW");
        Assert.Equal(1002, new LocalTestArchiveQueryService(store.DatabasePath)
            .QuerySlice(new LocalTestArchiveQuery(), 0, 1).TotalCount);
    }

    [Fact]
    public void QueryAll_TotalOrder_WithTiedTimestampAndCaseDuplicateIds_HasNoPageGapsOrDuplicates()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var modified = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var inserted = new List<TestCase>();
        for (var i = 1204; i >= 0; i--)
        {
            var uid = Guid.ParseExact($"00000000-0000-0000-0000-{i + 1:x12}", "D");
            var tc = NewTest(i, modified);
            tc.Uid = uid;
            tc.Id = i % 2 == 0 ? "DUPLICATE" : "duplicate";
            tc.UltimaModifica = modified;
            inserted.Add(tc);
            store.Upsert(Path.Combine(_root, $"total_{i:D4}.json"), modified, 100, tc);
        }

        var items = new LocalTestArchiveQueryService(store.DatabasePath)
            .QueryAll(new LocalTestArchiveQuery(), maximumRows: 2000);
        var expected = inserted.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Uid.ToString("N"), StringComparer.Ordinal)
            .Select(x => x.Uid);

        Assert.Equal(1205, items.Count);
        Assert.Equal(1205, items.Select(x => x.Uid).Distinct().Count());
        Assert.Equal(expected, items.Select(x => x.Uid));
    }

    [Fact]
    public void QueryAll_CreatedAtOrBefore_IncludeIlGiornoScelto_AncheConOrario()
    {
        // Data a del filtro Report a mezzanotte: il test creato alle 15:30 del giorno scelto deve restare nel risultato indicizzato.
        var store = new LocalTestIndexStore(_root, _local);
        var tc = NewTest(1, new DateTime(2026, 6, 1, 15, 30, 0));
        tc.DataCreazione = new DateTime(2026, 6, 1, 15, 30, 0);
        store.Upsert(Path.Combine(_root, "creato-nel-giorno-scelto.json"), tc.UltimaModifica, 100, tc);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        Assert.Contains(tc.Uid, service.QueryAll(new LocalTestArchiveQuery { CreatedAtOrBefore = new DateTime(2026, 6, 1) }).Select(x => x.Uid));
        Assert.DoesNotContain(tc.Uid, service.QueryAll(new LocalTestArchiveQuery { CreatedAtOrBefore = new DateTime(2026, 5, 31) }).Select(x => x.Uid));
    }

    [Fact]
    public void QueryAll_ReportSemantics_MatchesFallbackForOrdinalTextAndRawDateTimeKinds()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var created = new DateTime(2026, 6, 1, 0, 30, 0, DateTimeKind.Utc);
        var tc = NewTest(1, created.AddHours(1));
        tc.DataCreazione = created;
        tc.Componente = "ECM";
        tc.Veicolo = "Daily";
        tc.Veicoli = ["Daily"];
        tc.CampiCustom["Colore"] = "Rosso";
        store.Upsert(Path.Combine(_root, "report-semantics.json"), tc.UltimaModifica, 100, tc);

        var filters = new[]
        {
            new ReportFilter { Componente = ["ecm"] },
            new ReportFilter { Veicoli = ["daily"] },
            new ReportFilter { CampiCustom = { ["Colore"] = ["rosso"] } },
            new ReportFilter { DataDa = new DateTime(2026, 6, 1, 1, 0, 0, DateTimeKind.Local) },
            new ReportFilter { DataA = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified) },
            new ReportFilter
            {
                Componente = ["ECM"], Veicoli = ["Daily"],
                DataDa = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Local),
                DataA = new DateTime(2026, 6, 1, 1, 0, 0, DateTimeKind.Unspecified),
                CampiCustom = { ["Colore"] = ["Rosso"] }
            }
        };

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        foreach (var filter in filters)
        {
            var expected = filter.Corrisponde(tc) ? new[] { tc.Uid } : Array.Empty<Guid>();
            var actual = service.QueryAll(new LocalTestArchiveQuery
            {
                Componente = filter.Componente.SingleOrDefault(),
                Vehicle = filter.Veicoli.SingleOrDefault(),
                CreatedAtOrAfter = filter.DataDa,
                CreatedAtOrBefore = filter.DataA,
                CustomFieldSelections = filter.CampiCustom.ToDictionary(
                    x => x.Key, x => (IReadOnlyCollection<string>)x.Value),
                MatchStringsOrdinal = true
            }).Select(x => x.Uid);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ReportRawDateFilter_UsesProjectedIndexWithoutReadingPayloadJson()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var tc = NewTest(1, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        tc.DataCreazione = new DateTime(2026, 6, 1, 0, 30, 0, DateTimeKind.Unspecified);
        store.Upsert(Path.Combine(_root, "raw-date-plan.json"), tc.UltimaModifica, 100, tc);

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT s.uid
            FROM test_summary s
            WHERE s.eliminato = 0 AND s.data_creazione_raw_ticks >= $from
            ORDER BY s.ultima_modifica_ticks DESC, s.id COLLATE NOCASE, s.uid
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$from", tc.DataCreazione.Ticks);
        var details = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("ix_test_summary_created_raw", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("test_case_cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Query_WorkStateSet_IsAppliedBeforePagination()
    {
        var store = new LocalTestIndexStore(_root, _local);
        for (var i = 0; i < 40; i++)
        {
            var tc = NewTest(i, DateTime.UtcNow.AddMinutes(i));
            tc.StatoLavoro = (i % 3) switch
            {
                0 => StatoLavoroTest.Pianificato,
                1 => StatoLavoroTest.DaRitestare,
                _ => StatoLavoroTest.Concluso
            };
            store.Upsert(Path.Combine(_root, $"state_{i:D3}.json"), DateTime.UtcNow, 100, tc);
        }

        var page = new LocalTestArchiveQueryService(store.DatabasePath).Query(new LocalTestArchiveQuery
        {
            WorkStates = [StatoLavoroTest.Pianificato, StatoLavoroTest.DaRitestare],
            PageSize = 100
        });

        Assert.All(page.Items, tc => Assert.True(tc.DaFare));
        Assert.DoesNotContain(page.Items, tc => tc.StatoLavoro == StatoLavoroTest.Concluso);
    }

    [Fact]
    public void FilterCatalog_ComesFromProjectionAndExcludesDeletedTests()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var active = NewTest(1, DateTime.UtcNow);
        active.Componente = "ECM";
        active.Veicoli = ["Daily"];
        active.Veicolo = "Daily";
        active.Assegnatario = "Mario";
        active.CreatoDa = "Paolo";
        active.RequisitoId = ["REQ-1"];
        active.Funzionalita = ["Guasto"];
        store.Upsert(Path.Combine(_root, "active.json"), DateTime.UtcNow, 100, active);

        var deleted = NewTest(2, DateTime.UtcNow.AddMinutes(1));
        deleted.Eliminato = true;
        deleted.Componente = "HIDDEN-componente";
        deleted.Veicoli = ["HiddenVehicle"];
        deleted.Veicolo = "HiddenVehicle";
        store.Upsert(Path.Combine(_root, "deleted.json"), DateTime.UtcNow, 100, deleted);

        var catalog = new LocalTestArchiveQueryService(store.DatabasePath).GetFilterCatalog();

        Assert.Contains("ECM", catalog.Componente);
        Assert.DoesNotContain("HIDDEN-componente", catalog.Componente);
        Assert.Contains("Daily", catalog.Veicoli);
        Assert.Contains("Mario", catalog.Assegnatari);
        Assert.Contains("Paolo", catalog.Creatori);
        Assert.Contains("REQ-1", catalog.Requisiti);
        Assert.Contains("Guasto", catalog.Funzionalita);
    }

    /// <summary>Caso critico dell'ottimizzazione (feedback utente 2026-09-17, volumi 100k): un valore condiviso
    /// da un test attivo e da uno eliminato deve restare visibile — l'esclusione riguarda solo i valori
    /// referenziati ESCLUSIVAMENTE da test nel cestino, non ogni valore che il cestino tocca.</summary>
    [Fact]
    public void FilterCatalog_ValoreCondivisoTraTestAttivoEDeletedRestaVisibile()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var active = NewTest(1, DateTime.UtcNow);
        active.Componente = "BCM";
        store.Upsert(Path.Combine(_root, "active.json"), DateTime.UtcNow, 100, active);

        var deleted = NewTest(2, DateTime.UtcNow.AddMinutes(1));
        deleted.Eliminato = true;
        deleted.Componente = "BCM";
        store.Upsert(Path.Combine(_root, "deleted.json"), DateTime.UtcNow, 100, deleted);

        var catalog = new LocalTestArchiveQueryService(store.DatabasePath).GetFilterCatalog();
        Assert.Contains("BCM", catalog.Componente);
    }

    [Fact]
    public void EcuFilterValues_TracksUpdatesDeletionAndRestoreWithoutOtherCatalogs()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var active = NewTest(1, DateTime.UtcNow);
        active.Componente = "BCM";
        var other = NewTest(2, DateTime.UtcNow);
        other.Componente = new EcuSelection(new[] { "bcm", "DELETED" });
        other.Eliminato = true;
        var activePath = Path.Combine(_root, "active.json");
        var otherPath = Path.Combine(_root, "other.json");
        store.Upsert(activePath, DateTime.UtcNow, 100, active);
        store.Upsert(otherPath, DateTime.UtcNow, 100, other);
        Assert.Equal(service.GetFilterCatalog().Componente, service.GetEcuFilterValues());
        Assert.Single(service.GetEcuFilterValues());
        Assert.Contains("DELETED", service.GetEcuFilterValues(includeDeleted: true));
        active.Eliminato = true;
        store.Upsert(activePath, DateTime.UtcNow, 100, active);
        Assert.Empty(service.GetEcuFilterValues());
        other.Eliminato = false;
        store.Upsert(otherPath, DateTime.UtcNow, 100, other);
        Assert.Equal(2, service.GetEcuFilterValues().Count);
        store.Remove(otherPath);
        Assert.Empty(service.GetEcuFilterValues());
    }
    [Fact]
    public void HomeSummary_UsesSingleProjectionQueryAndMatchesWorkStateSemantics()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var states = new[]
        {
            StatoLavoroTest.Pianificato,
            StatoLavoroTest.InCorso,
            StatoLavoroTest.DaRitestare,
            StatoLavoroTest.Concluso
        };
        for (var i = 0; i < states.Length; i++)
        {
            var tc = NewTest(i, DateTime.UtcNow.AddMinutes(i));
            tc.StatoLavoro = states[i];
            tc.Assegnatario = i < 3 ? "Mario" : "Luigi";
            if (i >= 2)
            {
                // Decisione 6.4.8: "testato" = REV con almeno una Condizione realmente eseguita (la sola pianificazione non basta).
                var esito = i == 3 ? Esito.Conforme : Esito.NonConforme;
                tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.UtcNow, Esito = esito, Condizioni = [new Condizione { Esito = esito }] });
            }
            store.Upsert(Path.Combine(_root, $"summary_{i}.json"), DateTime.UtcNow, 100, tc);
        }

        var summary = new LocalTestArchiveQueryService(store.DatabasePath).GetHomeSummary("Mario");

        Assert.Equal(4, summary.TotalTests);
        Assert.Equal(1, summary.ToRetest);
        Assert.Equal(3, summary.MyActive);
        Assert.Equal(2, summary.Tested);
        Assert.Equal(1, summary.Conforming);
        Assert.Equal(50d, summary.ConformityRatePercent, 6);
        Assert.Equal(new StatiMatrice(1, 1, 0, 2, 0, 0), summary.TestStatusDistribution);
    }

    [Fact]
    public void HomeFunctionalitySources_ReducesHistoryAndMatchesReportWithManualCatalogAndVehicles()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var sameDate = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var tests = new List<TestCase>
        {
            TestConFunzionalita(1, "T-A", ["V1"], sameDate, Esito.NonConforme),
            TestConFunzionalita(2, "T-Z", ["V1"], sameDate, Esito.Conforme),
            TestConFunzionalita(3, "T-MULTI", ["V2", "V3"], sameDate.AddDays(1), Esito.ConformeConDeviazioni),
            TestConFunzionalita(4, "UnitLogger-IMPORT", ["V4"], sameDate.AddDays(2), Esito.NonConforme, OrigineTest.ImportStatoUdt),
            TestConFunzionalita(5, "T-PLAN", ["V5"], sameDate.AddDays(3), Esito.DaEseguire),
        };
        var deviazioneAccettata = TestConFunzionalita(6, "T-DEV-OVERRIDE", ["V8"], sameDate.AddDays(4), Esito.ConformeConDeviazioni);
        deviazioneAccettata.Esecuzioni[0].Esito = Esito.Conforme;
        deviazioneAccettata.Esecuzioni[0].EsitoSovrascritto = true;
        tests.Add(deviazioneAccettata);
        for (var i = 0; i < tests.Count; i++)
            store.Upsert(Path.Combine(_root, $"state_{i}.json"), sameDate, 100, tests[i]);

        var veicoli = Enumerable.Range(1, 8).Select(i => new Veicolo { Nome = $"V{i}" }).ToArray();
        var funzionalita = new[] { new Funzionalita { Nome = "F", TutteLeEcu = true } };
        var componente = new[]
        {
            new Componente { Nome = "BCM", VeicoliAssociati = ["V1", "V2", "V3", "V4", "V5", "V8"] },
            new Componente { Nome = "TCU", VeicoliAssociati = ["V6"] },
            new Componente { Nome = "ABS", VeicoliAssociati = ["V7"] },
        };
        var manuali = new[]
        {
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "BCM", Veicolo = "V1", Esito = Esito.NonConforme,
                Operatore = "Master", DataDecisione = sameDate, Nota = "manuale a parità di data"
            },
            new StatoFunzionalitaEcuVeicolo
            {
                Funzionalita = "F", Componente = "TCU", Veicolo = "V6", Esito = Esito.Conforme,
                Operatore = "Master", DataDecisione = sameDate, Nota = "solo manuale"
            },
        };
        var query = new LocalTestArchiveQueryService(store.DatabasePath);
        var sourceReads = 0;
        query.HomeFunctionalityReadObserver = () => sourceReads++;

        var sources = query.GetHomeFunctionalitySources();
        Assert.Equal("T-Z", Assert.Single(sources, s => s.Veicolo == "V1").TestId); // ordinal ID breaks the same-date Test tie.
        Assert.Equal(Esito.ConformeConDeviazioni, Assert.Single(sources, s => s.Veicolo == "V8").Outcome);
        Assert.Same(sources, query.GetHomeFunctionalitySources());
        Assert.Equal(1, sourceReads);
        var actual = HomeConformitySummaryService.CalcolaStatoCorrente(sources, veicoli, funzionalita, componente, manuali);
        var expected = ReportMatriceService.Genera(tests,
            new ConfigurazioneMatriceReport { CampiX = [DimensioniMatrice.Componente], CampiY = [DimensioniMatrice.Funzionalita] },
            veicoli, funzionalita, [], null, componente, manuali).TotaleGenerale.StatiPercentuale;

        Assert.Equal(6, sources.Count); // one row per winning TestCase source key, including both V2/V3 and the imported snapshot.
        Assert.Equal(expected, actual);
        Assert.Equal(1, actual.NonTestato); // ABS/V7 has no Test or manual value.
        Assert.Equal(1, actual.DaEseguire); // BCM/V5 is planned.
        Assert.Equal(2, actual.NonConforme); // the same-date manual entry wins for BCM/V1; the imported snapshot is also included.
        Assert.Equal(new StatiMatrice(2, 1, 1, 1, 0, 0), query.GetHomeSummary("Mario").TestStatusDistribution);

        tests[1].Esecuzioni[0].Esito = Esito.NonConforme;
        tests[1].Esecuzioni[0].Condizioni[0].Esito = Esito.NonConforme;
        store.Upsert(Path.Combine(_root, "state_1.json"), sameDate, 100, tests[1]);
        Assert.NotSame(sources, query.GetHomeFunctionalitySources());
        Assert.Equal(2, sourceReads); // an index commit invalidates the cached single-pass source projection.
    }

    [Fact]
    public void RequirementCoverage_MatchesLegacyService_ForStandardD4DAndUntested()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var standard = new Requisito { Id = "REQ-STD", ApplicabileBanco = true, ApplicabileVeicolo = true };
        var untested = new Requisito { Id = "REQ-EMPTY", ApplicabileBanco = true, ApplicabileVeicolo = true };
        var extra = new Requisito { Id = "REQ-Extra", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, ["Daily", "Alpha"]);

        var tests = new List<TestCase>();
        var oldStandard = NewTest(1, now.AddMinutes(-20));
        oldStandard.RequisitoId = [standard.Id];
        oldStandard.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(-20), Esito = Esito.NonConforme }];
        tests.Add(oldStandard);

        var newStandard = NewTest(2, now.AddMinutes(-10));
        newStandard.RequisitoId = [standard.Id];
        newStandard.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(-10), Esito = Esito.Conforme }];
        tests.Add(newStandard);

        var daily = NewTest(3, now.AddMinutes(-8));
        daily.RequisitoId = [extra.Id];
        daily.Veicoli = ["Daily"];
        daily.Veicolo = "Daily";
        daily.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(-8), Esito = Esito.ConformeConDeviazioni }];
        tests.Add(daily);

        var alpha = NewTest(4, now.AddMinutes(-5));
        alpha.RequisitoId = [extra.Id];
        alpha.Veicoli = ["Alpha"];
        alpha.Veicolo = "Alpha";
        alpha.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(-5), Esito = Esito.NonConforme }];
        tests.Add(alpha);

        foreach (var tc in tests)
            store.Upsert(Path.Combine(_root, tc.Id + ".json"), tc.UltimaModifica, 100, tc);

        var requisiti = new List<Requisito> { standard, extra, untested };
        var expected = CoverageService.PerRequisito(requisiti, tests, soloBanco: false, soloVeicolo: false);
        var actual = new LocalTestArchiveQueryService(store.DatabasePath)
            .GetRequirementCoverage(requisiti, soloBanco: false, soloVeicolo: false);

        Assert.Equal(expected.Conformi, actual.Conformi);
        Assert.Equal(expected.ConformiConDeviazioni, actual.ConformiConDeviazioni);
        Assert.Equal(expected.NonConformi, actual.NonConformi);
        Assert.Equal(expected.NonTestati, actual.NonTestati);
        Assert.Equal(expected.PercentualeCompletamento, actual.PercentualeCompletamento, 6);
    }

    [Fact]
    public void HomeWorkPageQuery_UsesTotalOrderCompositeIndexWithoutTemporarySort()
    {
        var store = new LocalTestIndexStore(_root, _local);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT s.uid
            FROM test_summary s
            WHERE s.eliminato = 0 AND s.stato_lavoro = $state
            ORDER BY s.ultima_modifica_ticks DESC, s.id COLLATE NOCASE, s.uid
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$state", (int)StatoLavoroTest.Concluso);

        var details = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) details.Add(reader.GetString(3));

        Assert.Contains(details, detail => detail.Contains("ix_test_summary_work_page", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RequirementCoverage_WithOnlyStandardRequirements_SkipsD4DRankingAndMatchesLegacy()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var standard = new Requisito { Id = "REQ-STD-ONLY", ApplicabileBanco = true, ApplicabileVeicolo = true };
        var untested = new Requisito { Id = "REQ-UNTESTED", ApplicabileBanco = true, ApplicabileVeicolo = true };
        var test = NewTest(1, now);
        test.RequisitoId = [standard.Id];
        test.Esecuzioni = [new Esecuzione { Data = now, Esito = Esito.Conforme }];
        store.Upsert(Path.Combine(_root, "coverage_standard_only.json"), now, 100, test);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var d4dReads = 0;
        service.RequirementCoverageD4DReadObserver = () => d4dReads++;
        var requisiti = new List<Requisito> { standard, untested };

        var actual = service.GetRequirementCoverage(requisiti);
        var expected = CoverageService.PerRequisito(requisiti, [test], soloBanco: false, soloVeicolo: false);

        Assert.Equal(0, d4dReads);
        Assert.Equal(expected.Conformi, actual.Conformi);
        Assert.Equal(expected.NonTestati, actual.NonTestati);
        Assert.Equal(expected.PercentualeCompletamento, actual.PercentualeCompletamento, 6);
    }

    [Fact]
    public void RequirementCoverage_WithoutAnyRevision_SkipsBothRankingsAndCountsRequirementsAsUntested()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var standard = new Requisito { Id = "REQ-STD-NO-REV", ApplicabileBanco = true, ApplicabileVeicolo = true };
        var extra = new Requisito { Id = "REQ-Extra-NO-REV", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, ["Daily", "Alpha"]);
        var test = NewTest(11, DateTime.UtcNow);
        test.RequisitoId = [standard.Id, extra.Id];
        test.Veicoli = ["Daily"];
        test.Veicolo = "Daily";
        store.Upsert(Path.Combine(_root, "coverage_without_revisions.json"), test.UltimaModifica, 100, test);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var standardReads = 0;
        var d4dReads = 0;
        service.RequirementCoverageStandardReadObserver = () => standardReads++;
        service.RequirementCoverageD4DReadObserver = () => d4dReads++;
        var requisiti = new List<Requisito> { standard, extra };

        var actual = service.GetRequirementCoverage(requisiti);
        var expected = CoverageService.PerRequisito(requisiti, [test], soloBanco: false, soloVeicolo: false);

        Assert.Equal(0, standardReads);
        Assert.Equal(0, d4dReads);
        Assert.Equal(expected.NonTestati, actual.NonTestati);
        Assert.Equal(3, actual.NonTestati); // un requisito standard + due sottoveicoli Extra
        Assert.Equal(0, actual.PercentualeCompletamento);
    }

    [Fact]
    public void RequirementCoverage_ConcurrentWriter_DoesNotMixStandardAndD4DSnapshots()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var requisito = new Requisito { Id = "REQ-Extra-SNAPSHOT", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, ["Daily"]);

        var precedente = NewTest(1, now);
        precedente.RequisitoId = [requisito.Id];
        precedente.Veicolo = "Daily";
        precedente.Veicoli = ["Daily"];
        precedente.Esecuzioni = [new Esecuzione { Data = now, Esito = Esito.NonConforme }];
        store.Upsert(Path.Combine(_root, "coverage_old.json"), now, 100, precedente);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var d4dReads = 0;
        service.RequirementCoverageD4DReadObserver = () => d4dReads++;
        service.RequirementCoverageStandardReadObserver = () =>
        {
            var nuovo = NewTest(2, now.AddMinutes(1));
            nuovo.RequisitoId = [requisito.Id];
            nuovo.Veicolo = "Daily";
            nuovo.Veicoli = ["Daily"];
            nuovo.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(1), Esito = Esito.Conforme }];
            store.Upsert(Path.Combine(_root, "coverage_new.json"), now.AddMinutes(1), 100, nuovo);
        };

        var snapshot = service.GetRequirementCoverage([requisito]);

        Assert.Equal(1, snapshot.NonConformi);
        Assert.Equal(0, snapshot.Conformi);
        Assert.Equal(1, d4dReads);
        Assert.Equal(1, new LocalTestArchiveQueryService(store.DatabasePath)
            .GetRequirementCoverage([requisito]).Conformi);
    }

    [Fact]
    public void DefaultDashboardSnapshot_MatchesLegacyKpiAndAggregateCounts()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var tests = new List<TestCase>();
        for (var i = 0; i < 6; i++)
        {
            var tc = NewTest(i, DateTime.UtcNow.AddMinutes(i));
            tc.Veicoli = [i % 2 == 0 ? "Daily" : "Alpha"];
            tc.Veicolo = tc.Veicoli[0];
            tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.UtcNow.AddMinutes(i), Operatore = i % 2 == 0 ? "Mario" : "Luigi", Esito = Esito.NonConforme, Condizioni = [new Condizione { Esito = Esito.NonConforme }] });
            if (i % 3 != 0)
                tc.Esecuzioni.Add(new Esecuzione { Data = DateTime.UtcNow.AddMinutes(i + 1), Operatore = "Mario", Esito = Esito.Conforme, Condizioni = [new Condizione { Esito = Esito.Conforme }] });
            tests.Add(tc);
            store.Upsert(Path.Combine(_root, $"dash_{i}.json"), tc.UltimaModifica, 100, tc);
        }

        var snapshot = new LocalTestArchiveQueryService(store.DatabasePath).GetDefaultDashboardSnapshot();
        var expectedKpi = KpiService.CalcolaAggregato(tests);

        Assert.Equal(expectedKpi.MediaIterazioni, snapshot.IterationKpi.MediaIterazioni, 6);
        Assert.Equal(expectedKpi.CasiRaggiuntiConforme, snapshot.IterationKpi.CasiRaggiuntiConforme);
        Assert.Equal(expectedKpi.CasiMaiRaggiuntiConforme, snapshot.IterationKpi.CasiMaiRaggiuntiConforme);
        Assert.Equal(3, snapshot.VehicleCounts["Daily"]);
        Assert.Equal(3, snapshot.VehicleCounts["Alpha"]);
        Assert.Equal(tests.SelectMany(t => t.Esecuzioni).Count(e => e.Operatore == "Mario"), snapshot.ExecutionCountsByOperator["Mario"]);
        Assert.Equal(tests.Count(t => t.StatoCorrente == Esito.Conforme), snapshot.OutcomeCounts[Esito.Conforme]);
    }

    [Fact]
    public void CustomFieldValues_AreReadFromSqliteJsonWithoutHydratingArchive()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var a = NewTest(1, DateTime.UtcNow);
        a.CampiCustom["Fleet"] = "Alpha";
        var b = NewTest(2, DateTime.UtcNow.AddMinutes(1));
        b.CampiCustom["Fleet"] = "Beta";
        var c = NewTest(3, DateTime.UtcNow.AddMinutes(2));
        c.CampiCustom["Other"] = "Ignored";
        store.Upsert(Path.Combine(_root, "custom-a.json"), DateTime.UtcNow, 100, a);
        store.Upsert(Path.Combine(_root, "custom-b.json"), DateTime.UtcNow, 100, b);
        store.Upsert(Path.Combine(_root, "custom-c.json"), DateTime.UtcNow, 100, c);

        var values = new LocalTestArchiveQueryService(store.DatabasePath).GetCustomFieldValues("Fleet");

        Assert.Equal(new[] { "Alpha", "Beta" }, values);
    }

    /// <summary>
    /// Addendum prestazioni 2026-09-25 (Home a 100k): GetHomeAggiornaBatch sostituisce, con UNA sola connessione,
    /// le quattro-cinque chiamate separate che Aggiorna() faceva (componente, KPI testata, copertura requisiti, campi
    /// custom, Da fare/Fatti). Il dataset copre gli invarianti della dashboard: Test importati (Origine =
    /// ImportStatoUdt) esclusi dai conteggi, Test cancellati esclusi dai cataloghi, piu' componente distinte, un campo
    /// custom e le due pagine di lavoro. Il batch deve produrre ESATTAMENTE gli stessi risultati delle chiamate
    /// individuali preesistenti, mai un'approssimazione "quasi uguale".
    /// </summary>
    [Fact]
    public void GetHomeAggiornaBatch_MatchesIndividualQueries_AcrossMixedDataset()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;

        var testloomPianificato = NewTest(1, now.AddMinutes(-10));
        testloomPianificato.Componente = "ECM";
        testloomPianificato.Assegnatario = "Mario";
        testloomPianificato.StatoLavoro = StatoLavoroTest.Pianificato;
        testloomPianificato.CampiCustom["Fleet"] = "Alpha";
        store.Upsert(Path.Combine(_root, "t1.json"), testloomPianificato.UltimaModifica, 100, testloomPianificato);

        var testloomConcluso = NewTest(2, now.AddMinutes(-8));
        testloomConcluso.Componente = "BCM";
        testloomConcluso.StatoLavoro = StatoLavoroTest.Concluso;
        testloomConcluso.Esecuzioni.Add(new Esecuzione { Data = now.AddMinutes(-8), Esito = Esito.Conforme });
        testloomConcluso.CampiCustom["Fleet"] = "Beta";
        store.Upsert(Path.Combine(_root, "t2.json"), testloomConcluso.UltimaModifica, 100, testloomConcluso);

        var importato = NewTest(3, now.AddMinutes(-6));
        importato.Componente = "TCU";
        importato.Origine = OrigineTest.ImportStatoUdt;
        importato.StatoLavoro = StatoLavoroTest.Concluso;
        importato.Esecuzioni.Add(new Esecuzione { Data = now.AddMinutes(-6), Esito = Esito.Conforme });
        store.Upsert(Path.Combine(_root, "t3.json"), importato.UltimaModifica, 100, importato);

        var cancellato = NewTest(4, now.AddMinutes(-4));
        cancellato.Componente = "HIDDEN-componente";
        cancellato.Eliminato = true;
        store.Upsert(Path.Combine(_root, "t4.json"), cancellato.UltimaModifica, 100, cancellato);

        var requisito = new Requisito { Id = "REQ-1", ApplicabileBanco = true, ApplicabileVeicolo = true };
        testloomConcluso.RequisitoId = [requisito.Id];
        store.Upsert(Path.Combine(_root, "t2.json"), testloomConcluso.UltimaModifica, 101, testloomConcluso);
        var requisiti = new List<Requisito> { requisito };

        var service = new LocalTestArchiveQueryService(store.DatabasePath);

        var todoQuery = new LocalTestArchiveQuery
        {
            WorkStates = new[] { StatoLavoroTest.Pianificato, StatoLavoroTest.InCorso, StatoLavoroTest.DaRivedere, StatoLavoroTest.InAttesa, StatoLavoroTest.DaRitestare },
            Page = 1,
            PageSize = 100
        };
        var doneQuery = new LocalTestArchiveQuery { WorkState = StatoLavoroTest.Concluso, Page = 1, PageSize = 100 };

        var batch = service.GetHomeAggiornaBatch(new LocalHomeAggiornaBatchRequest
        {
            IncludeEcu = true,
            ActiveOperator = "Mario",
            Requisiti = requisiti,
            CustomFieldKeys = new[] { "Fleet" },
            TodoQuery = todoQuery,
            DoneQuery = doneQuery
        });

        var expectedEcu = service.GetEcuFilterValues();
        var expectedSummary = service.GetHomeSummary("Mario");
        var expectedCoverage = service.GetRequirementCoverage(requisiti, soloBanco: false, soloVeicolo: false);
        var expectedFleet = service.GetCustomFieldValues("Fleet");
        var expectedTodo = service.Query(todoQuery);
        var expectedDone = service.Query(doneQuery);

        Assert.Equal(expectedEcu, batch.Componente);
        Assert.DoesNotContain("HIDDEN-componente", batch.Componente);
        // Test importati: esclusi dal conteggio testata (TotalTests=2, non 3), invariante di dashboard.
        Assert.Equal(2, batch.Summary.TotalTests);
        Assert.Equal(expectedSummary.TotalTests, batch.Summary.TotalTests);
        Assert.Equal(expectedSummary.MyActive, batch.Summary.MyActive);
        Assert.Equal(expectedSummary.Conforming, batch.Summary.Conforming);
        Assert.NotNull(batch.Coverage);
        Assert.Equal(expectedCoverage.PercentualeCompletamento, batch.Coverage!.PercentualeCompletamento, 6);
        Assert.Equal(expectedFleet, batch.CustomFieldValues["Fleet"]);
        Assert.NotNull(batch.Todo);
        Assert.NotNull(batch.Done);
        Assert.Equal(expectedTodo.TotalCount, batch.Todo!.TotalCount);
        Assert.Equal(expectedTodo.Items.Select(t => t.Id), batch.Todo.Items.Select(t => t.Id));
        Assert.Equal(expectedDone.TotalCount, batch.Done!.TotalCount);
        Assert.Equal(expectedDone.Items.Select(t => t.Id), batch.Done.Items.Select(t => t.Id));
    }

    /// <summary>
    /// Addendum prestazioni 2026-09-26 (Home a 100k, rivisita): GetRequirementCoverage/GetHomeAggiornaBatch
    /// cachano ora il bucket per (Generazione, PRAGMA data_version, requisiti, soloBanco, soloVeicolo) — stessa
    /// politica già in produzione per LocalHomeDashboardQueryService. Una rivisita a dati invariati non deve
    /// ripetere ne' il ranking standard ne' quello Extra: gli observer, entrambi già usati per verificare quante
    /// volte le query vengono davvero eseguite, devono restare a 1 dopo una seconda chiamata identica.
    /// </summary>
    [Fact]
    public void GetRequirementCoverage_SecondIdenticalCall_ReusesCacheWithoutRereadingIndex()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var extra = new Requisito { Id = "REQ-CACHE-Extra", ApplicabileBanco = true, ApplicabileVeicolo = true };
        RequisitoApplicabilita.ImpostaTipo(extra, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(extra, ["Daily"]);

        var test = NewTest(1, now);
        test.RequisitoId = [extra.Id];
        test.Veicolo = "Daily";
        test.Veicoli = ["Daily"];
        test.Esecuzioni = [new Esecuzione { Data = now, Esito = Esito.Conforme }];
        store.Upsert(Path.Combine(_root, "cache_d4d.json"), now, 100, test);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var standardReads = 0;
        var d4dReads = 0;
        service.RequirementCoverageStandardReadObserver = () => standardReads++;
        service.RequirementCoverageD4DReadObserver = () => d4dReads++;

        var requisiti = new List<Requisito> { extra };
        var prima = service.GetRequirementCoverage(requisiti);
        var seconda = service.GetRequirementCoverage(requisiti);

        Assert.Equal(1, standardReads);
        Assert.Equal(1, d4dReads);
        Assert.Equal(prima.Conformi, seconda.Conformi);
        Assert.Equal(prima.NonTestati, seconda.NonTestati);
        Assert.Equal(prima.PercentualeCompletamento, seconda.PercentualeCompletamento, 6);

        // Una scrittura sulla stessa cartella (qui: stesso processo, come nei test "seconda postazione" del
        // repository) cambia PRAGMA data_version e deve far ricalcolare, non restituire il vecchio bucket.
        var nuovoTest = NewTest(2, now.AddMinutes(1));
        nuovoTest.RequisitoId = [extra.Id];
        nuovoTest.Veicolo = "Daily";
        nuovoTest.Veicoli = ["Daily"];
        nuovoTest.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(1), Esito = Esito.NonConforme }];
        store.Upsert(Path.Combine(_root, "cache_d4d_nuovo.json"), now.AddMinutes(1), 100, nuovoTest);

        var terza = service.GetRequirementCoverage(requisiti);
        Assert.Equal(2, standardReads);
        Assert.Equal(2, d4dReads);
        Assert.Equal(1, terza.NonConformi);
        Assert.Equal(0, terza.Conformi);
    }

    /// <summary>
    /// Addendum prestazioni 2026-09-26 (Home a 100k, rivisita): a differenza di Coverage, Componente e Summary dentro
    /// GetHomeAggiornaBatch non avevano ALCUNA cache — venivano rilette a ogni Aggiorna() anche a dati identici.
    /// Stesso schema del test coverage sopra: una rivisita identica non deve ripetere la lettura (observer fermo
    /// a 1), una scrittura (che cambia PRAGMA data_version) deve invece farla ripetere e restituire il dato
    /// nuovo — mai una cache basata su data/dimensione file, solo su Generazione+revisione+parametri.
    /// </summary>
    [Fact]
    public void GetHomeAggiornaBatch_SecondIdenticalCall_ReusesEcuESummaryWithoutRereadingIndex()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;

        var test = NewTest(1, now);
        test.Componente = "ECM";
        test.Assegnatario = "Mario";
        test.StatoLavoro = StatoLavoroTest.Pianificato;
        store.Upsert(Path.Combine(_root, "ecu_summary_cache.json"), now, 100, test);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var reads = 0;
        service.EcuSummaryReadObserver = () => reads++;

        var request = new LocalHomeAggiornaBatchRequest { IncludeEcu = true, ActiveOperator = "Mario" };
        var prima = service.GetHomeAggiornaBatch(request);
        var seconda = service.GetHomeAggiornaBatch(request);

        Assert.Equal(1, reads);
        Assert.Equal(prima.Componente, seconda.Componente);
        Assert.Equal(prima.Summary.TotalTests, seconda.Summary.TotalTests);
        Assert.Equal(prima.Summary.MyActive, seconda.Summary.MyActive);

        // Una scrittura (stesso schema "seconda postazione" usato altrove) cambia PRAGMA data_version e deve
        // far ricalcolare Componente/Summary, non restituire il valore cachato prima della scrittura.
        var nuovoTest = NewTest(2, now.AddMinutes(1));
        nuovoTest.Componente = "BCM-NUOVA";
        nuovoTest.Assegnatario = "Mario";
        nuovoTest.StatoLavoro = StatoLavoroTest.InCorso;
        store.Upsert(Path.Combine(_root, "ecu_summary_cache_nuovo.json"), now.AddMinutes(1), 100, nuovoTest);

        var terza = service.GetHomeAggiornaBatch(request);
        Assert.Equal(2, reads);
        Assert.Contains("BCM-NUOVA", terza.Componente);
        Assert.Equal(2, terza.Summary.TotalTests);
    }

    /// <summary>Un requisito editato (tipo/sottoveicoli/applicabilità) nel catalogo master non tocca l'indice
    /// SQLite Test (è un catalogo JSON separato): la cache deve accorgersene dal contenuto del Requisito
    /// stesso (vedi ComputeRequisitiHash), non da data_version, altrimenti la copertura resterebbe quella del
    /// requisito precedente dopo un semplice editing senza scritture sull'archivio Test.</summary>
    [Fact]
    public void GetRequirementCoverage_RequisitoEditatoDaStandardAD4D_NonRiusaLaCacheDelPrecedenteTipo()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var requisito = new Requisito { Id = "REQ-CACHE-EDIT", ApplicabileBanco = true, ApplicabileVeicolo = true };

        var testDaily = NewTest(1, now);
        testDaily.RequisitoId = [requisito.Id];
        testDaily.Veicolo = "Daily";
        testDaily.Veicoli = ["Daily"];
        testDaily.Esecuzioni = [new Esecuzione { Data = now, Esito = Esito.Conforme }];
        store.Upsert(Path.Combine(_root, "cache_edit_daily.json"), now, 100, testDaily);

        var testSway = NewTest(2, now.AddMinutes(1));
        testSway.RequisitoId = [requisito.Id];
        testSway.Veicolo = "Alpha";
        testSway.Veicoli = ["Alpha"];
        testSway.Esecuzioni = [new Esecuzione { Data = now.AddMinutes(1), Esito = Esito.NonConforme }];
        store.Upsert(Path.Combine(_root, "cache_edit_sway.json"), now.AddMinutes(1), 100, testSway);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);

        // Standard: cross-sottoveicolo, il ranking prende la revisione più recente in assoluto (Alpha, NonConforme).
        var standard = service.GetRequirementCoverage([requisito]);
        Assert.Equal(1, standard.NonConformi);
        Assert.Equal(0, standard.Conformi);

        // Stesso Requisito.Id ma diventato Extra limitato a "Daily", senza alcuna scrittura sull'indice Test:
        // deve rivalutare (Conforme su Daily), non restituire il bucket standard cachato.
        RequisitoApplicabilita.ImpostaTipo(requisito, TipoRequisito.Extra);
        RequisitoApplicabilita.ImpostaSottoveicoli(requisito, ["Daily"]);
        var extra = service.GetRequirementCoverage([requisito]);

        Assert.Equal(1, extra.Conformi);
        Assert.Equal(0, extra.NonConformi);
    }

    [Fact]
    public void CustomFields_MigrateV2CacheAndTrackEditDeleteRestoreAndRemoval()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var tc = NewTest(1, DateTime.UtcNow);
        tc.CampiCustom["Fleet"] = "Alpha";
        tc.CampiCustom["Blank"] = "   ";
        var path = Path.Combine(_root, "custom.json");
        store.ReconcileFull([new LocalIndexWriteEntry(path, DateTime.UtcNow, 100, tc)], DateTime.UtcNow);
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE test_custom_field; UPDATE meta SET value = '2' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }
        store = new LocalTestIndexStore(_root, _local);
        Assert.True(store.HasCompletedFullScan);
        var query = new LocalTestArchiveQueryService(store.DatabasePath);
        Assert.Equal(new[] { "Alpha" }, query.GetCustomFieldValues("Fleet"));
        Assert.Empty(query.GetCustomFieldValues("Blank"));
        Assert.Empty(query.GetCustomFieldValues("fleet"));
        tc.CampiCustom["Fleet"] = "Beta";
        store.Upsert(path, DateTime.UtcNow, 101, tc);
        Assert.Equal(new[] { "Beta" }, query.GetCustomFieldValues("Fleet"));
        tc.Eliminato = true;
        store.Upsert(path, DateTime.UtcNow, 102, tc);
        Assert.Empty(query.GetCustomFieldValues("Fleet"));
        tc.Eliminato = false;
        store.Upsert(path, DateTime.UtcNow, 103, tc);
        Assert.Equal(new[] { "Beta" }, query.GetCustomFieldValues("Fleet"));
        tc.CampiCustom.Clear();
        store.Upsert(path, DateTime.UtcNow, 104, tc);
        Assert.Empty(query.GetCustomFieldValues("Fleet"));
        tc.CampiCustom["Fleet"] = "Gamma";
        store.Upsert(path, DateTime.UtcNow, 105, tc);
        store.Remove(path);
        Assert.Empty(query.GetCustomFieldValues("Fleet"));
    }
    /// <summary>Correzione prestazioni 6.4.0 (pagina Test a freddo, 100k): il catalogo filtri della Lista Test
    /// è cachato per (Generazione, PRAGMA data_version, includeDeleted). Una rivisita a indice invariato non
    /// rilegge; qualunque scrittura sull'indice (nuovo Test, rimozione, Test spostato nel cestino) deve invece
    /// far rileggere e restituire i valori aggiornati — mai una cache basata su data/dimensione file.</summary>
    [Fact]
    public void GetFilterCatalog_RivisitaInvariataRiusaLaCache_OgniScritturaLaInvalida()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var primo = NewTest(1, now);
        primo.Componente = "ECM";
        primo.Veicolo = "Daily";
        primo.Veicoli = ["Daily"];
        primo.RequisitoId = ["REQ-1"];
        primo.Assegnatario = "Mario";
        store.Upsert(Path.Combine(_root, "filtri_primo.json"), now, 100, primo);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var reads = 0;
        service.FilterCatalogReadObserver = () => reads++;

        var prima = service.GetFilterCatalog();
        var seconda = service.GetFilterCatalog();
        Assert.Equal(1, reads);
        Assert.Equal(prima.Componente, seconda.Componente);
        Assert.Equal(prima.Requisiti, seconda.Requisiti);

        // includeDeleted fa parte della chiave: non deve riusare il catalogo dei soli attivi.
        service.GetFilterCatalog(includeDeleted: true);
        Assert.Equal(2, reads);

        var nuovo = NewTest(2, now.AddMinutes(1));
        nuovo.Componente = "BCM-NUOVA";
        nuovo.Veicolo = "Alpha";
        nuovo.Veicoli = ["Alpha"];
        nuovo.RequisitoId = ["REQ-NUOVO"];
        nuovo.Assegnatario = "Giulia";
        var pathNuovo = Path.Combine(_root, "filtri_nuovo.json");
        store.Upsert(pathNuovo, now.AddMinutes(1), 100, nuovo);

        var terza = service.GetFilterCatalog();
        Assert.Equal(3, reads);
        Assert.Contains("BCM-NUOVA", terza.Componente);
        Assert.Contains("Alpha", terza.Veicoli);
        Assert.Contains("REQ-NUOVO", terza.Requisiti);
        Assert.Contains("Giulia", terza.Assegnatari);

        // Test spostato nel cestino: i valori usati solo da lui spariscono dal catalogo dei soli attivi.
        nuovo.Eliminato = true;
        nuovo.RevisioneDati = 2;
        store.Upsert(pathNuovo, now.AddMinutes(2), 101, nuovo);
        var quarta = service.GetFilterCatalog();
        Assert.Equal(4, reads);
        Assert.DoesNotContain("BCM-NUOVA", quarta.Componente);
        Assert.DoesNotContain("REQ-NUOVO", quarta.Requisiti);

        store.Remove(pathNuovo);
        var quinta = service.GetFilterCatalog(includeDeleted: true);
        Assert.Equal(5, reads);
        Assert.DoesNotContain("BCM-NUOVA", quinta.Componente);
        Assert.Contains("ECM", quinta.Componente);
    }

    /// <summary>
    /// P2.6: il vecchio cache key era PRAGMA data_version, contatore PER CONNESSIONE. Dopo una scrittura, una connessione
    /// nuova puo' riportare lo stesso valore della cache precedente pur leggendo dati cambiati: il catalogo stantio veniva
    /// servito. Con la revisione nel meta, letta nella stessa transazione delle query, la rilettura non puo' essere saltata.
    /// </summary>
    [Fact]
    public void GetFilterCatalog_ConnessioneNuovaDopoUnaScrittura_NonRiusaLaCacheStantia()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        var primo = NewTest(1, now);
        primo.Componente = "ECM";
        primo.Veicolo = "Daily";
        primo.Veicoli = ["Daily"];
        primo.RequisitoId = ["REQ-1"];
        primo.Assegnatario = "Mario";
        store.Upsert(Path.Combine(_root, "stale_primo.json"), now, 100, primo);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var reads = 0;
        service.FilterCatalogReadObserver = () => reads++;
        Assert.Contains("ECM", service.GetFilterCatalog().Componente);
        Assert.Equal(1, reads);

        var nuovo = NewTest(2, now.AddMinutes(1));
        nuovo.Componente = "BCM-NUOVA";
        nuovo.Veicolo = "Alpha";
        nuovo.Veicoli = ["Alpha"];
        nuovo.RequisitoId = ["REQ-NUOVO"];
        nuovo.Assegnatario = "Giulia";
        store.Upsert(Path.Combine(_root, "stale_nuovo.json"), now.AddMinutes(1), 100, nuovo);

        // Pool svuotato: la rilettura usa una connessione nuova, che non ha mai visto la cache precedente.
        SqliteConnection.ClearAllPools();
        var dopo = service.GetFilterCatalog();
        Assert.Equal(2, reads);
        Assert.Contains("BCM-NUOVA", dopo.Componente);
    }

    [Fact]
    public void QueryAll_Keyset_MatchesOffsetPaging_WithTiedValuesDeletedRowsAndCaseDuplicateIds()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var baseUtc = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);
        var random = new Random(20261010);
        var tests = new List<TestCase>();
        for (var i = 0; i < 2350; i++)
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            var tc = NewTest(i, baseUtc);
            tc.Uid = new Guid(bytes);
            tc.Id = (i % 3 == 0 ? "TC-" : "tc-") + $"{i % 60:D3}";   // id duplicati esatti e duplicati solo di maiuscole
            tc.UltimaModifica = baseUtc.AddHours(i % 9);             // nove istanti: legami molto frequenti
            tc.Eliminato = i % 4 == 0;                               // cestino
            tc.Origine = i % 7 == 0 ? OrigineTest.ImportStatoUdt : OrigineTest.TestLoom;
            tc.Assegnatario = i % 5 == 0 ? "Mario" : "Luigi";
            tests.Add(tc);
        }
        // Inserimento in ordine casuale rispetto a uid e id: l'ordine di QueryAll non puo' dipendere dal rowid.
        foreach (var (test, indice) in tests.Select((x, n) => (x, n)).OrderBy(_ => random.Next()))
            store.Upsert(Path.Combine(_root, $"keyset_{indice:D4}.json"), test.UltimaModifica, 100, test);

        var service = new LocalTestArchiveQueryService(store.DatabasePath);
        var varianti = new[]
        {
            new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.All },
            new LocalTestArchiveQuery(),
            new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.DeletedOnly },
            new LocalTestArchiveQuery { Assignee = "Mario" },
            new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.All, Origine = OrigineTest.ImportStatoUdt },
        };
        foreach (var query in varianti)
        {
            var conOffset = PaginazioneAOffset(service, query);
            var conKeyset = service.QueryAll(query);
            Assert.Equal(conOffset.Select(Firma), conKeyset.Select(Firma));
        }

        // Oracolo indipendente dal SQL per l'archivio intero: stessa chiave (ultima modifica DESC, id senza
        // distinzione di maiuscole, uid) calcolata in C#. Id e uid sono ASCII senza '_', quindi i confronti coincidono.
        var attesi = tests
            .OrderByDescending(x => x.UltimaModifica.Ticks)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Uid.ToString("N"), StringComparer.Ordinal)
            .Select(x => x.Uid)
            .ToList();
        var pagine = new List<long>();
        service.QueryAllPageReadObserver = offset => pagine.Add(offset);
        var tutti = service.QueryAll(new LocalTestArchiveQuery { DeletionScope = LocalTestDeletionScope.All }, maximumRows: 5000);
        service.QueryAllPageReadObserver = null;

        Assert.Equal(attesi, tutti.Select(x => x.Uid));
        Assert.Equal(tests.Count, tutti.Select(x => x.Uid).Distinct().Count());
        Assert.Equal(new long[] { 0, 1000, 2000 }, pagine);
    }

    [Fact]
    public void IndiceOrdineArchivio_SiCreaSuIndiceLocaleEsistenteSenzaCambiareIlRisultato()
    {
        var store = new LocalTestIndexStore(_root, _local);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 30; i++)
            store.Upsert(Path.Combine(_root, $"migrazione_{i:D3}.json"), now, 100, NewTest(i, now.AddMinutes(i % 4)));
        var prima = new LocalTestArchiveQueryService(store.DatabasePath).QueryAll(new LocalTestArchiveQuery()).Select(x => x.Uid).ToList();
        Assert.Equal(1, CountIndiciPerNome(store.DatabasePath, "ix_test_summary_ordine_archivio"));

        // Indice locale scritto da una versione precedente: l'indice nuovo manca e va creato alla riapertura.
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP INDEX ix_test_summary_ordine_archivio;";
            drop.ExecuteNonQuery();
        }
        Assert.Equal(0, CountIndiciPerNome(store.DatabasePath, "ix_test_summary_ordine_archivio"));

        var riaperto = new LocalTestIndexStore(_root, _local);
        Assert.Equal(1, CountIndiciPerNome(riaperto.DatabasePath, "ix_test_summary_ordine_archivio"));
        var dopo = new LocalTestArchiveQueryService(riaperto.DatabasePath).QueryAll(new LocalTestArchiveQuery()).Select(x => x.Uid).ToList();
        Assert.Equal(prima, dopo);
    }

    /// <summary>Riferimento: il vecchio percorso di QueryAll, cioe' pagine a OFFSET da MaxPageSize sullo stesso ordine.</summary>
    private static List<TestCase> PaginazioneAOffset(LocalTestArchiveQueryService service, LocalTestArchiveQuery query)
    {
        var risultato = new List<TestCase>();
        var totale = service.QuerySlice(query, 0, 1).TotalCount;
        for (long offset = 0; offset < totale; offset += LocalTestArchiveQueryService.MaxPageSize)
            risultato.AddRange(service.QuerySlice(query, offset, LocalTestArchiveQueryService.MaxPageSize).Items);
        return risultato;
    }

    /// <summary>Contenuto osservabile di un Test, per confrontare sequenze complete e non solo gli uid.</summary>
    private static string Firma(TestCase tc) =>
        $"{tc.Uid:N}|{tc.Id}|{tc.UltimaModifica.Ticks}|{tc.Eliminato}|{tc.Assegnatario}|{tc.Origine}|{tc.DescrizioneBreve}";

    private static long CountIndiciPerNome(string databasePath, string nome)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $nome;";
        command.Parameters.AddWithValue("$nome", nome);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static TestCase NewTest(int index, DateTime modified) => new()
    {
        Uid = Guid.NewGuid(),
        Id = $"TC-{index:D4}",
        DataCreazione = modified.AddDays(-1),
        UltimaModifica = modified,
        RevisioneDati = 1,
        CreatoDa = "Paolo",
        DescrizioneBreve = "Test " + index,
        Descrizione = "Archive query test " + index,
        StatoLavoro = StatoLavoroTest.Pianificato
    };

    private static TestCase TestConFunzionalita(int index, string id, IReadOnlyList<string> veicoli, DateTime data,
        Esito esito, OrigineTest origine = OrigineTest.TestLoom)
    {
        var test = NewTest(index, data);
        test.Id = id;
        test.Componente = new EcuSelection(["BCM"]);
        test.Veicoli = veicoli.ToList();
        test.Veicolo = veicoli.FirstOrDefault() ?? "";
        test.Funzionalita = ["F"];
        test.Origine = origine;
        test.Esecuzioni =
        [
            new Esecuzione
            {
                Data = data,
                Esito = esito,
                Condizioni = [new Condizione { Funzionalita = "F", Componente = new EcuSelection(["BCM"]), Esito = esito }],
            }
        ];
        return test;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_local, recursive: true); } catch { }
    }
}


