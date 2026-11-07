using TestLoom.App.Models;
using TestLoom.App.Services;

namespace TestLoom.App.Tests;

public sealed class PolarionReportServiceTests
{
    private static readonly DateTimeOffset Baseline = new(2026, 10, 28, 8, 0, 0, TimeSpan.Zero);
    private static PolarionStatusHistoryService.Observation Event(string status, int minutes) =>
        new() { Status = status, FirstSeenUtc = Baseline.AddMinutes(minutes) };

    [Fact]
    public void UltimoCommentoEsta_CacheLeggeIlPiuRecenteEMostraSoloTestoSicuro()
    {
        var now = new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero);
        var snapshot = new PolarionTicketSnapshot
        {
            Id = "TICKET-77", ProjectId = "TICKET_Tickets", Status = "testing", RetrievedAtUtc = now,
            Comments = new List<PolarionCommentSnapshot>
            {
                new() { Id = "old", AuthorId = "tester", Created = now.AddDays(-1), Text = "<p>Older note</p>" },
                new() { Id = "new", AuthorId = "developer", Created = now, Text = "<p>Validazione &amp; analisi guasto <strong>completata</strong></p>" }
            }
        };
        var row = Assert.Single(PolarionReportService.Build(
            [new PolarionReportService.Association("TICKET-77", "NC", "NC-77", [])],
            _ => snapshot, _ => null, new Dictionary<string, string>()));
        Assert.Equal(2, row.CommentsCount);
        Assert.Equal("developer", row.LatestCommentAuthor);
        Assert.Contains("Validazione & analisi guasto", row.LatestCommentPreview);
        Assert.Contains("completata", row.LatestCommentPreview);
        Assert.DoesNotContain("<", row.LatestCommentPreview);
        Assert.DoesNotContain("Older note", row.LatestCommentPreview);
    }

    [Fact]
    public void ReportEsta_FiltriVeicoliEGammeRichiedonoCorrispondenzaEsatta()
    {
        var rows = new[]
        {
            new PolarionReportService.Row
            {
                TicketId = "TICKET-1", Status = "testing",
                Vehicles = "Daily MY24, Eurocargo MY25", Ranges = "LCV, MCV"
            }
        };
        Assert.Single(PolarionReportService.Apply(rows,
            new PolarionReportService.Filter(Vehicle: "Daily MY24", Range: "LCV")));
        Assert.Empty(PolarionReportService.Apply(rows,
            new PolarionReportService.Filter(Vehicle: "Daily", Range: "LCV")));
        Assert.Empty(PolarionReportService.Apply(rows,
            new PolarionReportService.Filter(Vehicle: "Daily MY24", Range: "LC")));
    }

    [Fact]
    public void ReportEsta_RiepilogoMoltiTicket_ContaUnVoltaPerRecord()
    {
        var rows = Enumerable.Range(0, 10_000).Select(i => new PolarionReportService.Row
        {
            TicketId = $"TICKET-{i}", HasCache = i % 5 != 0,
            Status = i % 2 == 0 ? "closed" : "testing",
            HistoryUnavailable = i % 10 == 0,
            FirstClosedObservedAt = i % 4 == 0 ? Baseline : null
        }).ToArray();
        var result = PolarionReportService.Summarize(rows);
        Assert.Equal(10_000, result.Total);
        Assert.Equal(2_000, result.NotSynced);
        Assert.Equal(5_000, result.Closed);
        Assert.Equal(5_000, result.Testing);
        Assert.Equal(4_000, result.Open); // solo i Testing sincronizzati
        Assert.Equal(1_000, result.HistoriesUnavailable);
        Assert.Equal(2_500, result.ObservedClosures);
        Assert.Equal(2, result.StatusDistribution.Count);
    }

    [Fact]
    public void ReportEsta_CsvDaSnapshotFiltratoNeutralizzaFormule()
    {
        var text = "=HYPERLINK(\"https://example.invalid\")";
        var csv = TestLoom.App.ViewModels.PolarionLifecycleViewModel.PolarionCsv(
            [new PolarionReportService.Row { TicketId = "TICKET-123", Title = text }]);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.Contains("https://example.invalid", csv);
        Assert.Contains("\"\"https://example.invalid\"\"", csv); // quote escaping CSV
    }

    [Fact]
    public void MissingStatusIsUnknownRatherThanOpenEvenWithCache()
    {
        var associations = new[]
        {
            new PolarionReportService.Association("TICKET-1", "NC", "NC-1", []),
            new PolarionReportService.Association("TICKET-2", "NC", "NC-2", []),
            new PolarionReportService.Association("TICKET-3", "NC", "NC-3", [])
        };
        var rows = PolarionReportService.Build(associations,
            id => id == "TICKET-1" ? new PolarionTicketSnapshot { Status = null, RetrievedAtUtc = Baseline }
                : id == "TICKET-2" ? new PolarionTicketSnapshot { Status = "assigned", RetrievedAtUtc = Baseline } : null,
            _ => null, new Dictionary<string, string>());
        var summary = PolarionReportService.Summarize(rows);
        Assert.Equal(1, summary.Open);
        Assert.Equal(1, summary.NotSynced);
        Assert.Equal(2, summary.UnknownStatus);
        Assert.Contains(new[] { "Stato sconosciuto", "Unknown status", "Statut inconnu" }, x => rows.Single(r => r.TicketId == "TICKET-1").DataQuality.Contains(x, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportEsta_DataDigitataNonValida_NonMostraStatisticheGlobaliComeFiltrate()
    {
        using var state = TestStateFactory.Create();
        using var vm = new TestLoom.App.ViewModels.PolarionLifecycleViewModel(state, "NC");
        var rowsField = typeof(TestLoom.App.ViewModels.PolarionLifecycleViewModel)
            .GetField("_polarionAllRows",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(rowsField);
        var rows = Assert.IsType<List<PolarionReportService.Row>>(rowsField!.GetValue(vm));
        rows.Add(new PolarionReportService.Row { TicketId = "TICKET-1", Status = "testing" });

        vm.PolarionReportQuery = "TICKET";
        Assert.Equal(1, vm.PolarionReportCount);
        vm.PolarionReportFrom = "31/02/2026";
        Assert.NotEmpty(vm.PolarionDateFilterError);
        Assert.Equal(0, vm.PolarionReportCount);
        Assert.Empty(vm.PolarionReportRows);
        Assert.False(vm.PolarionCanExport);
    }

    [Fact]
    public void ReportEsta_StatoCacheDiversoDaStorico_VisibileMaSenzaInventareChiusure()
    {
        var id = "TICKET-987";
        var history = new PolarionStatusHistoryService.History
        {
            Observations = [Event("testing", 0)]
        };
        var row = Assert.Single(PolarionReportService.Build(
            [new PolarionReportService.Association(id, "NC", "NC-1", [])],
            _ => new PolarionTicketSnapshot
            {
                Id = id, Status = "closed", Resolution = "done",
                RetrievedAtUtc = Baseline.AddHours(1)
            },
            _ => history, new Dictionary<string, string>()));
        Assert.True(row.HistoryStatusMismatch);
        Assert.Equal("closed", row.Status); // Stato attuale della cache resta visibile
        Assert.Null(row.FirstClosedObservedAt); // Lo storico non dimostra la transizione
        Assert.Equal(0, row.ClosureTransitionsObserved);
        Assert.Contains(new[] { "discordante", "differs", "différent" },
            word => row.DataQuality.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConflictingHistoryIsExplicitlyReportedWithoutPretendingTimelineIsComplete()
    {
        var associations = new[] { new PolarionReportService.Association("TICKET-1", "NC", "NC-1", []) };
        var row = Assert.Single(PolarionReportService.Build(associations,
            _ => new PolarionTicketSnapshot { Status = "closed", RetrievedAtUtc = Baseline },
            _ => throw new InvalidDataException("conflicting synthetic histories"), new Dictionary<string, string>()));
        Assert.True(row.HistoryUnavailable);
        Assert.Equal(1, PolarionReportService.Summarize([row]).HistoriesUnavailable);
        Assert.Contains(row.ObservedTransitionsText, new[] { "Cronologia non disponibile", "History unavailable", "Historique indisponible" });
        Assert.Contains(new[] { "Cronologia non disponibile", "History unavailable", "Historique indisponible" }, x => row.DataQuality.Contains(x, StringComparison.Ordinal));
        Assert.Empty(row.Timeline);
        Assert.Null(row.FirstClosedObservedAt);
        Assert.Equal("closed", row.Status);
    }

    [Fact]
    public void MonthlyGroupsMatchTheLocalCalendarUsedByDateFilters()
    {
        var timestamp = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        var expected = timestamp.ToLocalTime().ToString("yyyy-MM",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected,
            TestLoom.App.ViewModels.PolarionLifecycleViewModel.LocalMonthKey(timestamp));
    }

    [Fact]
    public void DashboardMonthlyChartCreatesAccurateLeapYearAndYearEndFilters()
    {
        Assert.True(TestLoom.App.ViewModels.PolarionLifecycleViewModel.TryMonthRange(
            "2028-02", out var first, out var last));
        Assert.Equal("01/02/2028", first);
        Assert.Equal("29/02/2028", last);
        Assert.True(TestLoom.App.ViewModels.PolarionLifecycleViewModel.TryMonthRange(
            "2026-12", out first, out last));
        Assert.Equal("31/12/2026", last);
        Assert.False(TestLoom.App.ViewModels.PolarionLifecycleViewModel.TryMonthRange(
            "2026-13", out _, out _));
    }

    [Fact]
    public void DateFiltersCompareDisplayedLocalCalendarDays()
    {
        // Ticketing timestamps are UTC/offset-aware; on-screen dates are in local time.
        var utcTime = new DateTimeOffset(2026, 10, 29, 23, 30, 0, TimeSpan.Zero);
        var day = utcTime.ToLocalTime();
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-321", CreatedAt = utcTime
        };
        var date = new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        var selected = PolarionReportService.Apply([row],
            new PolarionReportService.Filter(DateFrom: date, DateTo: date));
        Assert.Single(selected);
    }

    [Fact]
    public void ReenteredTestingAfterBaselineIsNotHiddenInDisplay()
    {
        var row = new PolarionReportService.Row
        {
            AlreadyTestingAtBaseline = true,
            FirstTestingObservedAt = new DateTimeOffset(2026, 10, 28, 11, 0, 0, TimeSpan.Zero)
        };
        Assert.Contains("baseline", row.TestingText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nuovo passaggio", row.TestingText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedNcCrDrilldownUsesExactSourceIdNotTextSubstring()
    {
        Assert.True(TestLoom.App.ViewModels.PolarionLifecycleViewModel.MatchesSource("NC-10", "NC-10"));
        Assert.True(TestLoom.App.ViewModels.PolarionLifecycleViewModel.MatchesSource("cr-22", "CR-22"));
        Assert.False(TestLoom.App.ViewModels.PolarionLifecycleViewModel.MatchesSource("NC-100", "NC-10"));
        Assert.False(TestLoom.App.ViewModels.PolarionLifecycleViewModel.MatchesSource("CR-200", "CR-20"));
        Assert.True(TestLoom.App.ViewModels.PolarionLifecycleViewModel.MatchesSource("NC-10", null));
    }

    [Fact]
    public void DeduplicatesNcCrAndNeverClaimsAClosureDateFromInitialClosedSnapshot()
    {
        var association = new[]
        {
            new PolarionReportService.Association("TICKET-123", "NC", "NC-1", ["Daily MY24"]),
            new PolarionReportService.Association("TICKET-123", "CR", "CR-2", ["Daily MY24"])
        };
        var snapshot = new PolarionTicketSnapshot
        {
            Id = "TICKET-123", ProjectId = "TICKET_Tickets", Status = "closed",
            Created = Baseline.AddDays(-4), RetrievedAtUtc = Baseline
        };
        var hist = new PolarionStatusHistoryService.History
        {
            Observations = [Event("closed", 0)]
        };
        var rows = PolarionReportService.Build(association, _ => snapshot, _ => hist,
            new Dictionary<string, string> { ["Daily MY24"] = "LCV" });
        Assert.Single(rows);
        Assert.Equal("CR CR-2, NC NC-1", rows[0].Records);
        Assert.True(rows[0].AlreadyClosedAtBaseline);
        Assert.Null(rows[0].FirstClosedObservedAt);
        Assert.Contains("Già chiuso", rows[0].ClosedText);
    }

    [Fact]
    public void ObservedTestingClosureReopenAppearOnlyAfterStateTransitions()
    {
        var association = new[]
        {
            new PolarionReportService.Association("TICKET-456", "NC", "NC-1", ["Daily MY24"])
        };
        var history = new PolarionStatusHistoryService.History
        {
            Observations = [Event("assigned", 0), Event("testing", 15), Event("closed", 45), Event("assigned", 70)]
        };
        var rows = PolarionReportService.Build(association,
            _ => new PolarionTicketSnapshot { Id = "TICKET-456", Status = "assigned", RetrievedAtUtc = Baseline },
            _ => history, new Dictionary<string, string>());
        Assert.Equal(Baseline.AddMinutes(15), rows[0].FirstTestingObservedAt);
        Assert.Equal(Baseline.AddMinutes(45), rows[0].FirstClosedObservedAt);
        Assert.Equal(Baseline.AddMinutes(70), rows[0].LastReopenObservedAt);
        Assert.Equal(1, PolarionReportService.Summarize(rows).ObservedReopenings);
    }

    [Fact]
    public void CountObservedReentryAndClosureCyclesWithConservativeTiming()
    {
        var ids = new[] { new PolarionReportService.Association("TICKET-88", "NC", "NC-88", []) };
        var history = new PolarionStatusHistoryService.History
        {
            Observations = [
                Event("assigned", 0), Event("testing", 10), Event("closed", 20),
                Event("assigned", 30), Event("testing", 40), Event("closed", 50)
            ]
        };
        var row = Assert.Single(PolarionReportService.Build(ids,
            _ => new PolarionTicketSnapshot { Id = "TICKET-88", Created = Baseline.AddDays(-1) },
            _ => history, new Dictionary<string, string>()));
        Assert.Equal(2, row.TestingTransitionsObserved);
        Assert.Equal(2, row.ClosureTransitionsObserved);
        Assert.Equal(1, row.ReopenTransitionsObserved);
        Assert.Equal(Baseline.AddMinutes(10), row.FirstTestingObservedAt);
        Assert.Contains("limite superiore", row.ClosureUpperBoundText);
    }

    [Fact]
    public void NoDurationIsInventedAtBaselineOrWithInvertedDates()
    {
        var unknown = new PolarionReportService.Row
        {
            AlreadyClosedAtBaseline = true, CreatedAt = Baseline.AddDays(-2)
        };
        Assert.Null(unknown.ClosureUpperBoundHours);
        Assert.Equal("Non calcolabile", unknown.ClosureUpperBoundText);
        Assert.Null(new PolarionReportService.Row
        {
            CreatedAt = Baseline, FirstTestingObservedAt = Baseline.AddMinutes(-1)
        }.TestingUpperBoundHours);
    }

    [Fact]
    public void FirstTestingObservationIsNotMisrepresentedAsTransitionTime()
    {
        var id = "TICKET-444";
        var source = new[]
        {
            new PolarionReportService.Association(id, "CR", "CR-12", ["Daily MY24"])
        };
        var rows = PolarionReportService.Build(source,
            _ => new PolarionTicketSnapshot { Id = id, Status = "testing" },
            _ => new PolarionStatusHistoryService.History
            {
                Observations = [Event("testing", 0)]
            }, new Dictionary<string, string>());
        Assert.True(rows[0].AlreadyTestingAtBaseline);
        Assert.Null(rows[0].FirstTestingObservedAt);
        Assert.Contains("Già in Testing", rows[0].TestingText);
    }

    [Fact]
    public void DisplaysOriginalPolarionPriorityUsingItsP1ToP5Label()
    {
        var links = new[] { new PolarionReportService.Association("TICKET-50", "NC", "NC-5", []) };
        var rows = PolarionReportService.Build(links,
            _ => new PolarionTicketSnapshot
            {
                Id = "TICKET-50", Priority = "33.0", Severity = "major", Status = "testing"
            },
            _ => null, new Dictionary<string, string>());
        Assert.Equal("P2", rows[0].Priority);
        Assert.Equal("Major", rows[0].Severity);
        Assert.Single(PolarionReportService.Apply(rows, new PolarionReportService.Filter(
            Priority: "P2", Severity: "Major", Status: "testing")));
    }

    [Fact]
    public void ArchivedCrIsPresentHistoricallyAndCanBeExcludedByFilter()
    {
        var links = new[]
        {
            new PolarionReportService.Association("TICKET-77", "CR", "CR-7", ["Daily MY24"], Archived: true)
        };
        var rows = PolarionReportService.Build(links, _ => null, _ => null,
            new Dictionary<string, string>());
        Assert.Single(rows);
        Assert.True(rows[0].Archived);
        Assert.Single(PolarionReportService.Apply(rows, new PolarionReportService.Filter()));
        Assert.Empty(PolarionReportService.Apply(rows, new PolarionReportService.Filter(
            IncludeArchived: false)));
    }

    [Fact]
    public void UnknownCreatedDateIsNotSilentlyIncludedInDateFilteredResults()
    {
        var unknown = new PolarionReportService.Row { TicketId = "TICKET-1" };
        Assert.Empty(PolarionReportService.Apply([unknown], new PolarionReportService.Filter(
            DateFrom: Baseline.AddDays(-10))));
        Assert.Single(PolarionReportService.Apply([unknown], new PolarionReportService.Filter()));
    }

    [Fact]
    public void CanFilterClosureDetectionMonthWithoutConfusingCreatedDate()
    {
        var r = new PolarionReportService.Row
        {
            TicketId = "TICKET-123", Status = "closed", Resolution = "done", Severity = "major",
            CreatedAt = Baseline.AddDays(-30), FirstClosedObservedAt = Baseline.AddDays(2)
        };
        Assert.Single(PolarionReportService.Apply([r], new PolarionReportService.Filter(
            Severity: "major", Resolution: "done", DateFrom: Baseline, DateDimension: "Chiusura")));
        Assert.Empty(PolarionReportService.Apply([r], new PolarionReportService.Filter(
            DateFrom: Baseline, DateDimension: "Apertura")));
    }

    [Fact]
    public void FiltersByVehicleStatusAndDateWithoutDiscardingUnknownsGlobally()
    {
        var row = new PolarionReportService.Row
        {
            TicketId = "TICKET-123", Status = "closed", Vehicles = "Daily MY24",
            CreatedAt = Baseline.AddDays(-1), HasCache = true
        };
        Assert.Single(PolarionReportService.Apply([row], new PolarionReportService.Filter(
            Status: "closed", Vehicle: "Daily MY24", DateFrom: Baseline.AddDays(-3))));
        Assert.Empty(PolarionReportService.Apply([row], new PolarionReportService.Filter(
            Status: "testing", Vehicle: "Daily MY24")));
    }
}
