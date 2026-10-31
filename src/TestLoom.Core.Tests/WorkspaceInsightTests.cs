using TestLoom.Core;

namespace TestLoom.Core.Tests;

public sealed class WorkspaceInsightTests
{
    private static WorkspaceDocument Sample()
    {
        var doc = WorkspaceStore.Create("Sample", "blank");
        var req1 = new WorkspaceRequirement { Key = "REQ-1", Title = "Login" };
        var req2 = new WorkspaceRequirement { Key = "REQ-2", Title = "Export <b>" };
        doc.Requirements.AddRange(new[] { req1, req2 });
        var a = new WorkspaceTestCase { Title = "Login works", Description = "happy path" };
        a.RequirementIds.Add(req1.Id);
        a.Steps.Add(new WorkspaceTestStep { Action = "Enter password", ExpectedResult = "Dashboard" });
        a.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
        a.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Passed });
        var b = new WorkspaceTestCase { Title = "Export <script>alert(1)</script>" };
        b.Executions.Add(new WorkspaceExecution { Result = ExecutionResult.Failed });
        var c = new WorkspaceTestCase { Title = "Not run yet" };
        var d = new WorkspaceTestCase { Title = "Old", Archived = true };
        doc.TestCases.AddRange(new[] { a, b, c, d });
        doc.Defects.Add(new WorkspaceDefect { Key = "D-1", Title = "Crash", Severity = WorkspaceDefectSeverity.Critical, TestCaseId = b.Id });
        doc.Defects.Add(new WorkspaceDefect { Key = "D-2", Title = "Typo", Status = WorkspaceDefectStatus.Closed });
        return doc;
    }

    [Fact]
    public void StatisticsUseLatestExecutionOfActiveTests()
    {
        var s = WorkspaceStatistics.Calculate(Sample());
        Assert.Equal(3, s.ActiveTests);
        Assert.Equal(1, s.ArchivedTests);
        Assert.Equal((1, 1, 0, 1), (s.Passed, s.Failed, s.Blocked, s.NotRun));
        Assert.Equal(50, s.PassRatePercent);
        Assert.Equal(66.7, s.ExecutionProgressPercent);
        Assert.Equal(50, s.RequirementCoveragePercent);
        Assert.Equal(1, s.OpenDefects);
        Assert.Equal(1, s.OpenCriticalDefects);
        Assert.Contains("pass rate 50%", s.Summary);
    }

    [Fact]
    public void StatisticsOfAnEmptyWorkspaceAreZeroNotNaN()
    {
        var s = WorkspaceStatistics.Calculate(WorkspaceStore.Create("Empty", "blank"));
        Assert.Equal(0, s.PassRatePercent);
        Assert.Equal(0, s.RequirementCoveragePercent);
    }

    [Fact]
    public void FilterCombinesTextOutcomeAndArchive()
    {
        var doc = Sample();
        Assert.Equal(3, TestCaseFilter.Apply(doc, new TestCaseFilter()).Count);
        Assert.Equal(4, TestCaseFilter.Apply(doc, new TestCaseFilter(IncludeArchived: true)).Count);
        Assert.Single(TestCaseFilter.Apply(doc, new TestCaseFilter(Outcome: OutcomeFilter.Passed)));
        Assert.Single(TestCaseFilter.Apply(doc, new TestCaseFilter(Outcome: OutcomeFilter.NotRun)));
        Assert.Single(TestCaseFilter.Apply(doc, new TestCaseFilter(Text: "PASSWORD dashboard")));
        Assert.Single(TestCaseFilter.Apply(doc, new TestCaseFilter(Text: "req-1")));
        Assert.Empty(TestCaseFilter.Apply(doc, new TestCaseFilter(Text: "nothing like this")));
    }

    [Fact]
    public void FilterByRequirement()
    {
        var doc = Sample();
        var result = TestCaseFilter.Apply(doc, new TestCaseFilter(RequirementId: doc.Requirements[0].Id));
        Assert.Equal("Login works", Assert.Single(result).Title);
    }

    [Fact]
    public void HtmlReportEncodesEverythingAndLoadsNothingExternal()
    {
        var html = WorkspaceHtmlReport.Generate(Sample(), new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero));
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Export &lt;b&gt;", html);
        Assert.Contains("2026-01-02 03:04 UTC", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("default-src 'none'", html);
    }

    [Fact]
    public void BackupRoundTripsAndRefusesToOverwriteOrAcceptForeignArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), "TestLoom-Backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = new WorkspaceStore(Path.Combine(root, "src"));
            source.Save(Sample());
            var zip = Path.Combine(root, "backup.zip");
            WorkspaceBackup.Create(source, zip);

            var restoredDir = Path.Combine(root, "restored");
            var restored = WorkspaceBackup.Restore(zip, restoredDir);
            Assert.Equal(4, restored.TestCases.Count);
            Assert.Throws<IOException>(() => WorkspaceBackup.Restore(zip, restoredDir));

            var foreign = Path.Combine(root, "foreign.zip");
            using (var a = System.IO.Compression.ZipFile.Open(foreign, System.IO.Compression.ZipArchiveMode.Create))
            {
                var e = a.CreateEntry("../evil.txt");
                using var w = new StreamWriter(e.Open()); w.Write("x");
            }
            Assert.Throws<InvalidDataException>(() => WorkspaceBackup.Restore(foreign, Path.Combine(root, "other")));
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
